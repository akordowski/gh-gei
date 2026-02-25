using OctoshiftCLI.Commands;
using OctoshiftCLI.Extensions;

namespace OctoshiftCLI.GitlabToGithub.Commands.MigrateRepo;

public class MigrateRepoCommandArgs : CommandArgs
{
    public string DockerImage { get; set; }
    public string GitlabServerUrl { get; set; }
    public string GitlabApiUrl { get; set; }
    [Secret]
    public string GitlabPat { get; set; }
    public string GitlabUsername { get; set; }
    public string GitlabNamespace { get; set; }
    public string GitlabProject { get; set; }
    public string GitlabOnly { get; set; }
    public string GitlabExcept { get; set; }
    public string GitlabLockProjects { get; set; }
    public string GitlabWithoutRenumbering { get; set; }
    public string GitlabManifest { get; set; }
    public bool GitlabSslNoVerify { get; set; }
    public bool GitlabDebug { get; set; }
    [Secret]
    public string GithubPat { get; set; }
    public string GithubOrg { get; set; }
    public string GithubRepo { get; set; }
    public bool QueueOnly { get; set; }
    public string TargetRepoVisibility { get; set; }
    public string TargetApiUrl { get; set; }
    public string TargetUploadsUrl { get; set; }
    public string ArchiveUrl { get; set; }
    public string ArchivePath { get; set; }
    public bool KeepArchive { get; set; }

    public bool ShouldGenerateArchive() =>
        GitlabApiUrl.HasValue() && !ArchiveUrl.HasValue();

    public bool ShouldImportArchive() =>
        ArchiveUrl.HasValue() || GithubOrg.HasValue();

    public bool ShouldUploadArchive() =>
        ArchiveUrl.IsNullOrWhiteSpace() && GithubOrg.HasValue();
}
