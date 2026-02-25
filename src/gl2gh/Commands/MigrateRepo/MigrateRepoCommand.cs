using System;
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using OctoshiftCLI.Commands;
using OctoshiftCLI.Contracts;
using OctoshiftCLI.GitlabToGithub.Services;
using OctoshiftCLI.Services;

namespace OctoshiftCLI.GitlabToGithub.Commands.MigrateRepo;

public class MigrateRepoCommand : CommandBase<MigrateRepoCommandArgs, MigrateRepoCommandHandler>
{
    public MigrateRepoCommand() : base(
        name: "migrate-repo",
        description: "Invokes the GitHub API's to migrate the repo and all PR data" +
                     Environment.NewLine +
                     "Note: Expects GL_PAT and GH_PAT env variables or --gitlab-pat and --github-pat options to be set.")
    {
        AddOption(DockerImage);
        AddOption(GitlabServerUrl);
        AddOption(GitlabApiUrl);
        AddOption(GitlabPat);
        AddOption(GitlabUsername);
        AddOption(GitlabNamespace);
        AddOption(GitlabProject);
        AddOption(GitlabOnly);
        AddOption(GitlabExcept);
        AddOption(GitlabLockProjects.FromAmong("true", "false", "transient"));
        AddOption(GitlabWithoutRenumbering.FromAmong("issues", "merge_requests"));
        AddOption(GitlabManifest);
        AddOption(GitlabSslNoVerify);
        AddOption(GitlabDebug);
        AddOption(GithubPat);
        AddOption(GithubOrg);
        AddOption(GithubRepo);
        AddOption(QueueOnly);
        AddOption(TargetRepoVisibility.FromAmong("public", "private", "internal"));
        AddOption(TargetApiUrl);
        AddOption(TargetUploadsUrl);
        AddOption(Verbose);
        AddOption(ArchiveUrl);
        AddOption(ArchivePath);
        AddOption(KeepArchive);
    }

    public Option<string> DockerImage { get; } = new(
        name: "--docker-image",
        description: "The gl-exporter Docker image name (e.g. ghcr.io/ORG/gl-exporter:latest). If the Docker image is not provided the gl_exporter will be executed.");

    public Option<string> GitlabServerUrl { get; } = new(
        name: "--gitlab-server-url",
        getDefaultValue: () => GitLabSettings.DEFAULT_GITLAB_SERVER_URL,
        description: "The full URL of the GitLab Server to migrate from.");

    public Option<string> GitlabApiUrl { get; } = new(
        name: "--gitlab-api-url",
        getDefaultValue: () => GitLabSettings.DEFAULT_GITLAB_API_URL,
        description: "The URL of the GitLab API.");

    public Option<string> GitlabPat { get; } = new(
        name: "--gitlab-pat",
        description: "The GitLab PAT. If not set will be read from GL_PAT environment variable.");

    public Option<string> GitlabUsername { get; } = new(
        name: "--gitlab-username",
        description: "The GitLab username of a user with site admin privileges. If not set will be read from GL_USERNAME environment variable.");

    public Option<string> GitlabNamespace { get; } = new(
        name: "--gitlab-namespace",
        description: "The GitLab namespace to migrate.")
    {
        IsRequired = true
    };

    public Option<string> GitlabProject { get; } = new(
        name: "--gitlab-project",
        description: "The GitLab project to migrate.")
    {
        IsRequired = true
    };

    public Option<string> GitlabOnly { get; } = new(
        name: "--gitlab-only",
        description: "A comma-separated list of models to export. Valid values: issues, merge_requests, commit_comments, hooks, wiki.");

    public Option<string> GitlabExcept { get; } = new(
        name: "--gitlab-except",
        description: "A comma-separated list of models exclude from export. Valid values: issues, merge_requests, commit_comments, hooks, wiki.");

    public Option<string> GitlabLockProjects { get; } = new(
        name: "--gitlab-lock-projects",
        description: "Lock source project when migrating. Valid values: true, false, transient.");

    public Option<string> GitlabWithoutRenumbering { get; } = new(
        name: "--gitlab-without-renumbering",
        description: "Do not renumber either issues or merge requests. Valid values: issues, merge_requests.");

    public Option<string> GitlabManifest { get; } = new(
        name: "--gitlab-manifest",
        description: "A file of projects to export.");

    public Option<bool> GitlabSslNoVerify { get; } = new(
        name: "--gitlab-ssl-no-verify",
        description: "Do not validate the GitLab SSL certificate.");

    public Option<bool> GitlabDebug { get; } = new(
        name: "--gitlab-debug",
        description: "Enable debug logging.");

    public Option<string> GithubPat { get; } = new(
        name: "--github-pat",
        description: "The GitHub personal access token to be used for the migration. If not set will be read from GH_PAT environment variable.");

    public Option<string> GithubOrg { get; } = new("--github-org")
    {
        IsRequired = true
    };

    public Option<string> GithubRepo { get; } = new("--github-repo")
    {
        IsRequired = true
    };

    public Option<bool> QueueOnly { get; } = new(
        name: "--queue-only",
        description: "Only queues the migration, does not wait for it to finish. Use the wait-for-migration command to subsequently wait for it to finish and view the status.");

    public Option<string> TargetRepoVisibility { get; } = new(
        name: "--target-repo-visibility",
        description: "The visibility of the target repo. Defaults to private. Valid values are public, private, or internal.");

    public Option<string> TargetApiUrl { get; } = new(
        name: "--target-api-url",
        description: "The URL of the target API, if not migrating to github.com. Defaults to https://api.github.com");

    public Option<string> TargetUploadsUrl { get; } = new(
            name: "--target-uploads-url",
            description: "The URL of the target uploads API, if not migrating to github.com. Defaults to https://uploads.github.com");

    public Option<bool> Verbose { get; } = new("--verbose");

    public Option<string> ArchiveUrl { get; } = new(
        name: "--archive-url",
        description:
        "URL used to download GitLab migration archive. Only needed if you want to manually retrieve the archive instead of letting this CLI do that for you.");

    public Option<string> ArchivePath { get; } = new(
        name: "--archive-path",
        description: "Path to GitLab migration archive on disk.");

    public Option<bool> KeepArchive { get; } = new(
        name: "--keep-archive",
        description: "Keeps the export archive after successfully uploading it. By default, it will be automatically deleted.");

    public override MigrateRepoCommandHandler BuildHandler(MigrateRepoCommandArgs args, IServiceProvider sp)
    {
        if (args is null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        if (sp is null)
        {
            throw new ArgumentNullException(nameof(sp));
        }

        var log = sp.GetRequiredService<OctoLogger>();
        var targetGithubApiFactory = sp.GetRequiredService<ITargetGithubApiFactory>();
        var targetGithubApi = targetGithubApiFactory.Create(args.TargetApiUrl, args.TargetUploadsUrl, args.GithubPat);
        var environmentVariableProvider = sp.GetRequiredService<EnvironmentVariableProvider>();
        var fileSystemProvider = sp.GetRequiredService<FileSystemProvider>();
        var warningsCountLogger = sp.GetRequiredService<WarningsCountLogger>();
        var processRunner = sp.GetRequiredService<IProcessRunner>();

        return new MigrateRepoCommandHandler(
            log,
            targetGithubApi,
            environmentVariableProvider,
            fileSystemProvider,
            warningsCountLogger,
            processRunner);
    }
}
