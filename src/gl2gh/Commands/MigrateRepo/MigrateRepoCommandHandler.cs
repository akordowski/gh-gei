using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OctoshiftCLI.Commands;
using OctoshiftCLI.Extensions;
using OctoshiftCLI.GitlabToGithub.Services;
using OctoshiftCLI.Services;

namespace OctoshiftCLI.GitlabToGithub.Commands.MigrateRepo;

public class MigrateRepoCommandHandler : ICommandHandler<MigrateRepoCommandArgs>
{
    private readonly OctoLogger _log;
    private readonly GithubApi _githubApi;
    private readonly EnvironmentVariableProvider _environmentVariableProvider;
    private readonly FileSystemProvider _fileSystemProvider;
    private readonly WarningsCountLogger _warningsCountLogger;
    private readonly IProcessRunner _processRunner;
    private const int CHECK_MIGRATION_STATUS_DELAY_IN_MILLISECONDS = 60000;

    public MigrateRepoCommandHandler(
        OctoLogger log,
        GithubApi githubApi,
        EnvironmentVariableProvider environmentVariableProvider,
        FileSystemProvider fileSystemProvider,
        WarningsCountLogger warningsCountLogger,
        IProcessRunner processRunner)
    {
        _log = log;
        _githubApi = githubApi;
        _environmentVariableProvider = environmentVariableProvider;
        _fileSystemProvider = fileSystemProvider;
        _warningsCountLogger = warningsCountLogger;
        _processRunner = processRunner;
    }

    public async Task Handle(MigrateRepoCommandArgs args)
    {
        if (args is null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        ValidateOptions(args);

        var useDocker = false;

        // If the Docker image is not provided, we will assume that the gl_exporter is already installed and available
        // in the PATH.
        if (string.IsNullOrWhiteSpace(args.DockerImage))
        {
            await ValidateGlExporterAsync();
        }
        else
        {
            await ValidateDockerAsync(args.DockerImage);
            useDocker = true;
        }

        var exportId = 0L;
        var migrationSourceId = "";

        if (args.ShouldImportArchive())
        {
            var targetRepoExists = await _githubApi.DoesRepoExist(args.GithubOrg, args.GithubRepo);

            if (targetRepoExists)
            {
                throw new OctoshiftCliException($"A repository called {args.GithubOrg}/{args.GithubRepo} already exists");
            }

            migrationSourceId = await CreateMigrationSource(args);
        }

        if (args.ShouldGenerateArchive())
        {
            exportId = await GenerateArchive(useDocker, args);
        }

        if (args.ShouldUploadArchive())
        {
            try
            {
                var archiveFilePath = GetArchiveFilePath(args, exportId);
                args.ArchiveUrl = await UploadArchiveToGithub(args.GithubOrg, archiveFilePath);
            }
            finally
            {
                if (!args.KeepArchive)
                {
                    DeleteArchive(args.ArchivePath);
                }
            }
        }

        if (args.ShouldImportArchive())
        {
            await ImportArchive(args, migrationSourceId, args.ArchiveUrl);
        }
    }

    private async Task<string> CreateMigrationSource(MigrateRepoCommandArgs args)
    {
        _log.LogInformation("Creating Migration Source...");

        args.GithubPat ??= _environmentVariableProvider.TargetGithubPersonalAccessToken();
        var githubOrgId = await _githubApi.GetOrganizationId(args.GithubOrg);

        try
        {
            return await _githubApi.CreateGitlabMigrationSource(githubOrgId);
        }
        catch (OctoshiftCliException ex) when (ex.Message.Contains("not have the correct permissions to execute"))
        {
            var insufficientPermissionsMessage = InsufficientPermissionsMessageGenerator.Generate(args.GithubOrg);
            var message = $"{ex.Message}{insufficientPermissionsMessage}";
            throw new OctoshiftCliException(message, ex);
        }
    }

    private void DeleteArchive(string path)
    {
        try
        {
            _fileSystemProvider.DeleteIfExists(path);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogWarning($"Couldn't delete the archive. Error message: \"{ex.Message}\"");
            _log.LogVerbose(ex.ToString());
        }
    }

    private async Task<long> GenerateArchive(bool useDocker, MigrateRepoCommandArgs args)
    {
        var exportId = long.Parse($"{DateTime.UtcNow:yyyyMMddHHmmss}");
        var archiveFileName = GetArchiveFileName(exportId);

        string command;
        IReadOnlyList<KeyValuePair<string, string>> environmentVariables = null;

        if (useDocker)
        {
            command = GetGlExporterDockerCommand(args, archiveFileName);
        }
        else
        {
            command = GetGlExporterCommand(args, archiveFileName);
            environmentVariables = [
                new KeyValuePair<string, string>("GITLAB_API_ENDPOINT", args.GitlabApiUrl),
                new KeyValuePair<string, string>("GITLAB_USERNAME", args.GitlabUsername),
                new KeyValuePair<string, string>("GITLAB_API_PRIVATE_TOKEN", args.GitlabPat)
            ];
        }

        _log.LogInformation($"Export started. Export ID: {exportId}");

        _log.LogDebug("Run the command to export GitLab archive:");
        _log.LogDebug(command);

        var exitCode = await _processRunner.StartAsync(
                command,
                args.ArchivePath,
                environmentVariables,
                _log.LogInformation,
                _log.LogError)
            .ConfigureAwait(false);

        _log.LogDebug($"The command exited with the exit code {exitCode}.");

        if (exitCode != 0)
        {
            throw new OctoshiftCliException("GitLab export failed");
        }

        var archiveFilePath = GetArchiveFilePath(args, exportId);
        _log.LogInformation($"Export completed. Your migration archive should be ready at {archiveFilePath}");

        return exportId;
    }

    private string GenerateUploadArchiveName() => $"{Guid.NewGuid()}.tar.gz";

    private string GetArchiveFileName(long exportId) => $"GitLab_export_{exportId}.tar.gz";

    private string GetArchiveFilePath(MigrateRepoCommandArgs args, long exportId) =>
        Path.GetFullPath(Path.Combine(args.ArchivePath, GetArchiveFileName(exportId)));

    private static string GetGlExporterCommand(MigrateRepoCommandArgs args, string outFile)
    {
        var command = new StringBuilder()
            .AppendLine("gl_exporter")
            .AppendLine(CultureInfo.InvariantCulture, $"  --out-file \"{outFile}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"  --namespace \"{args.GitlabNamespace}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"  --project \"{args.GitlabProject}\"");

        if (!string.IsNullOrWhiteSpace(args.GitlabOnly))
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --only \"{args.GitlabOnly}\"");
        }

        if (!string.IsNullOrWhiteSpace(args.GitlabExcept))
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --except \"{args.GitlabExcept}\"");
        }

        if (args.GitlabLockProjects is not null)
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --lock-projects \"{args.GitlabLockProjects}\"");
        }

        if (args.GitlabWithoutRenumbering is not null)
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --without-renumbering \"{args.GitlabWithoutRenumbering}\"");
        }

        if (!string.IsNullOrWhiteSpace(args.GitlabManifest))
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --manifest \"{args.GitlabManifest}\"");
        }

        if (args.GitlabSslNoVerify)
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --ssl-no-verify");
        }

        if (args.GitlabDebug)
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --debug");
        }

        return command.ToString();
    }

    private static string GetGlExporterDockerCommand(MigrateRepoCommandArgs args, string outFile)
    {
        var command = new StringBuilder()
            .AppendLine("docker run")
            .AppendLine("  --rm")
            .AppendLine("  -i")
            .AppendLine(CultureInfo.InvariantCulture, $"  -e GITLAB_API_ENDPOINT=\"{args.GitlabApiUrl}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"  -e GITLAB_USERNAME=\"{args.GitlabUsername}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"  -e GITLAB_API_PRIVATE_TOKEN=\"{args.GitlabPat}\"")
            .AppendLine("  -v ${PWD}:/workspace")
            .AppendLine(CultureInfo.InvariantCulture, $"  {args.DockerImage}")
            .AppendLine("  gl_exporter")
            .AppendLine(CultureInfo.InvariantCulture, $"  --out-file \"{outFile}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"  --namespace \"{args.GitlabNamespace}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"  --project \"{args.GitlabProject}\"");

        if (!string.IsNullOrWhiteSpace(args.GitlabOnly))
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --only \"{args.GitlabOnly}\"");
        }

        if (!string.IsNullOrWhiteSpace(args.GitlabExcept))
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --except \"{args.GitlabExcept}\"");
        }

        if (args.GitlabLockProjects is not null)
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --lock-projects \"{args.GitlabLockProjects}\"");
        }

        if (args.GitlabWithoutRenumbering is not null)
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --without-renumbering \"{args.GitlabWithoutRenumbering}\"");
        }

        if (!string.IsNullOrWhiteSpace(args.GitlabManifest))
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --manifest \"{args.GitlabManifest}\"");
        }

        if (args.GitlabSslNoVerify)
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --ssl-no-verify");
        }

        if (args.GitlabDebug)
        {
            command = command.AppendLine(CultureInfo.InvariantCulture, $"  --debug");
        }

        return command.ToString();
    }

    private string GetGitlabRepoUrl(MigrateRepoCommandArgs args) =>
        args.GitlabServerUrl.HasValue() && args.GitlabNamespace.HasValue() && args.GitlabProject.HasValue()
            ? $"{args.GitlabServerUrl.TrimEnd('/')}/{args.GitlabNamespace}/{args.GitlabProject}"
            : "https://not-used";

    private async Task<string> UploadArchiveToGithub(string org, string archivePath)
    {
        await using var archiveData = _fileSystemProvider.OpenRead(archivePath);
        var githubOrgDatabaseId = await _githubApi.GetOrganizationDatabaseId(org);

        _log.LogInformation("Uploading archive to GitHub Storage");
        var keyName = GenerateUploadArchiveName();
        var authenticatedGitArchiveUri = await _githubApi.UploadArchiveToGithubStorage(githubOrgDatabaseId, keyName, archiveData);

        return authenticatedGitArchiveUri;
    }

    private async Task ImportArchive(MigrateRepoCommandArgs args, string migrationSourceId, string archiveUrl = null)
    {
        _log.LogInformation("Importing Archive...");

        archiveUrl ??= args.ArchiveUrl;

        var gitlabRepoUrl = GetGitlabRepoUrl(args);

        args.GithubPat ??= _environmentVariableProvider.TargetGithubPersonalAccessToken();
        var githubOrgId = await _githubApi.GetOrganizationId(args.GithubOrg);

        string migrationId;

        try
        {
            migrationId = await _githubApi.StartGitlabMigration(migrationSourceId, gitlabRepoUrl, githubOrgId, args.GithubRepo, args.GithubPat, archiveUrl, args.TargetRepoVisibility);
        }
        catch (OctoshiftCliException ex) when (ex.Message == $"A repository called {args.GithubOrg}/{args.GithubRepo} already exists")
        {
            _log.LogWarning($"The Org '{args.GithubOrg}' already contains a repository with the name '{args.GithubRepo}'. No operation will be performed");
            return;
        }

        if (args.QueueOnly)
        {
            _log.LogInformation($"A repository migration (ID: {migrationId}) was successfully queued.");
            return;
        }

        var (migrationState, _, warningsCount, failureReason, migrationLogUrl) = await _githubApi.GetMigration(migrationId);

        while (RepositoryMigrationStatus.IsPending(migrationState))
        {
            _log.LogInformation($"Migration in progress (ID: {migrationId}). State: {migrationState}. Waiting 60 seconds...");
            await Task.Delay(CHECK_MIGRATION_STATUS_DELAY_IN_MILLISECONDS);
            (migrationState, _, warningsCount, failureReason, migrationLogUrl) = await _githubApi.GetGitLabMigration(migrationId);
        }

        var migrationLogAvailableMessage = $"Migration log available at {migrationLogUrl} or by running `gh {CliContext.RootCommand} download-logs --github-org {args.GithubOrg} --github-repo {args.GithubRepo}`";

        if (RepositoryMigrationStatus.IsFailed(migrationState))
        {
            _log.LogError($"Migration Failed. Migration ID: {migrationId}");
            _warningsCountLogger.LogWarningsCount(warningsCount);
            _log.LogInformation(migrationLogAvailableMessage);
            throw new OctoshiftCliException(failureReason);
        }

        _log.LogSuccess($"Migration completed (ID: {migrationId})! State: {migrationState}");
        _warningsCountLogger.LogWarningsCount(warningsCount);
        _log.LogInformation(migrationLogAvailableMessage);
    }

    private async Task ValidateDockerAsync(string dockerImage)
    {
        var isDockerAvailable = await IsDockerAvailableAsync();

        if (!isDockerAvailable)
        {
            throw new OctoshiftCliException("Docker is not available.");
        }

        var isDockerImageAvailable = await IsDockerImageAvailableAsync(dockerImage);

        if (!isDockerImageAvailable)
        {
            throw new OctoshiftCliException($"The Docker image {dockerImage} is not available.");
        }
    }

    private async Task ValidateGlExporterAsync()
    {
        var isGlExporterAvailable = await IsGlExporterAvailableAsync();

        if (!isGlExporterAvailable)
        {
            throw new OctoshiftCliException("gl_exporter is not available.");
        }
    }

    private void ValidateOptions(MigrateRepoCommandArgs args)
    {
        if (args.ShouldGenerateArchive())
        {
            if (GetGlUsername(args).IsNullOrWhiteSpace())
            {
                throw new OctoshiftCliException("GitLab username must be either set as GL_USERNAME environment variable or passed as --gl-username.");
            }

            if (GetGlPat(args).IsNullOrWhiteSpace())
            {
                throw new OctoshiftCliException("GitLab PAT must be either set as GL_PAT environment variable or passed as --gl-pat.");
            }

            if (args.ArchivePath.IsNullOrWhiteSpace())
            {
                args.ArchivePath = ".";
            }
            else
            {
                if (!Directory.Exists(args.ArchivePath))
                {
                    Directory.CreateDirectory(args.ArchivePath);
                }
            }
        }
    }

    private string GetGlUsername(MigrateRepoCommandArgs args) =>
        args.GitlabUsername.HasValue()
            ? args.GitlabUsername
            : _environmentVariableProvider.GitlabUsername(false);

    private string GetGlPat(MigrateRepoCommandArgs args) =>
        args.GitlabPat.HasValue()
            ? args.GitlabPat
            : _environmentVariableProvider.GitlabPersonalAccessToken(false);

    private async Task<bool> IsDockerAvailableAsync()
    {
        const string command = "docker -v";
        var outputData = new List<string>();

        _log.LogDebug($"Run the command '{command}' to check if the Docker is available.");

        var exitCode = await _processRunner.StartAsync(
                command,
                ".",
                outputDataReceived: output =>
                {
                    outputData.Add(output);
                    _log.LogDebug(output);
                },
                errorDataReceived: _log.LogError)
            .ConfigureAwait(false);

        _log.LogDebug($"The command exited with the exit code {exitCode}.");

        return exitCode == 0 &&
               outputData.Any(str => str.StartsWith("Docker version", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> IsDockerImageAvailableAsync(string dockerImage)
    {
        var command = $"docker images --filter=reference=\"{dockerImage}\"";
        var outputData = new List<string>();

        _log.LogDebug($"Run the command '{command}' to check if the Docker image is available.");

        var exitCode = await _processRunner.StartAsync(
                command,
                ".",
                outputDataReceived: output =>
                {
                    outputData.Add(output);
                    _log.LogDebug(output);
                },
                errorDataReceived: _log.LogError)
            .ConfigureAwait(false);

        _log.LogDebug($"The command exited with the exit code {exitCode}.");

        return exitCode == 0 &&
               outputData.Any(str => str.Contains(dockerImage, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> IsGlExporterAvailableAsync()
    {
        const string command = "gl_exporter --version";
        var outputData = new List<string>();

        _log.LogDebug($"Run the command '{command}' to check if the gl_exporter is available.");

        var exitCode = await _processRunner.StartAsync(
                command,
                ".",
                outputDataReceived: output =>
                {
                    outputData.Add(output);
                    _log.LogDebug(output);
                },
                errorDataReceived: _log.LogError)
            .ConfigureAwait(false);

        _log.LogDebug($"The command exited with the exit code {exitCode}.");

        return exitCode == 0 &&
               outputData.Any(str => str.Contains("Gitlab Exporter", StringComparison.OrdinalIgnoreCase));
    }
}
