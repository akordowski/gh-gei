using System;
using System.Threading.Tasks;

namespace OctoshiftCLI.GitlabToGithub.Services;

public interface IProcessRunner
{
    Task<int> StartAsync(
        string command,
        string workingDirectory,
        Action<string>? outputDataReceived = null,
        Action<string>? errorDataReceived = null);
}
