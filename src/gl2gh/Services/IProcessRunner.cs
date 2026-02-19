using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OctoshiftCLI.GitlabToGithub.Services;

public interface IProcessRunner
{
    Task<int> StartAsync(
        string command,
        string workingDirectory,
        IReadOnlyList<KeyValuePair<string, string>> environmentVariables = null,
        Action<string> outputDataReceived = null,
        Action<string> errorDataReceived = null);
}
