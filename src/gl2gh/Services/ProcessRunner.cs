using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OctoshiftCLI.GitlabToGithub.Services;

public class ProcessRunner : IProcessRunner
{
    public async Task<int> StartAsync(
        string command,
        string workingDirectory,
        IReadOnlyList<KeyValuePair<string, string>> environmentVariables = null,
        Action<string> outputDataReceived = null,
        Action<string> errorDataReceived = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string shell, shellArgs;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            shell = "powershell.exe";
            shellArgs = $"-NoProfile -ExecutionPolicy Bypass -Command \"{EscapeForPowerShell(command)}\"";
        }
        else
        {
            shell = "/bin/bash";
            shellArgs = $"-c \"{EscapeForBash(command)}\"";
        }

        var redirectStandardOutput = outputDataReceived is not null;
        var redirectStandardError = errorDataReceived is not null;

        var processStartInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = shellArgs,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = redirectStandardError,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        if (environmentVariables?.Count > 0)
        {
            foreach (var environmentVariable in environmentVariables)
            {
                processStartInfo.EnvironmentVariables[environmentVariable.Key] = environmentVariable.Value;
            }
        }

        using var process = new Process();
        process.StartInfo = processStartInfo;
        process.Start();

        if (redirectStandardOutput)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    outputDataReceived!(e.Data);
                }
            };
            process.BeginOutputReadLine();
        }

        if (redirectStandardError)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    errorDataReceived!(e.Data);
                }
            };
            process.BeginErrorReadLine();
        }

        await process.WaitForExitAsync().ConfigureAwait(false);

        return process.ExitCode;
    }

    private static string EscapeForPowerShell(string command) =>
        command
            .Replace(Environment.NewLine, string.Empty, StringComparison.InvariantCulture)
            .Replace("\"", "\\\"", StringComparison.InvariantCulture);

    private static string EscapeForBash(string command) =>
        command
            .Replace(Environment.NewLine, string.Empty, StringComparison.InvariantCulture)
            .Replace("\"", "'", StringComparison.InvariantCulture);
}
