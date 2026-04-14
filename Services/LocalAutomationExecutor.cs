using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalAutomationExecutor
{
    public async Task<LocalAutomationExecutionResult> ExecuteAsync(LocalAutomationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var result = new LocalAutomationExecutionResult
        {
            Success = true,
            Plan = plan,
            Message = plan.Summary
        };
        var trace = new StringBuilder();

        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ExecuteAction(action, result, cancellationToken);
            var lastMessage = result.ActionResults.LastOrDefault()?.Message;
            trace.AppendLine($"{action.Type}: {(string.IsNullOrWhiteSpace(lastMessage) ? "ok" : lastMessage)}");
        }

        result.Trace = trace.ToString().TrimEnd();
        return result;
    }

    private static void ExecuteAction(LocalAutomationAction action, LocalAutomationExecutionResult result, CancellationToken cancellationToken)
    {
        var source = LocalAutomationPathResolver.NormalizePathOrNull(action.Source);
        var destination = LocalAutomationPathResolver.NormalizePathOrNull(action.Destination);
        var path = LocalAutomationPathResolver.NormalizePathOrNull(action.Path);

        switch (action.Type)
        {
            case LocalAutomationActionType.CreateDirectory:
                Directory.CreateDirectory(path!);
                AddResult(result, action.Type, path, $"Created directory '{path}'.");
                break;

            case LocalAutomationActionType.Copy:
                File.Copy(source!, destination!, action.Overwrite);
                AddResult(result, action.Type, destination, $"Copied '{source}' to '{destination}'.");
                break;

            case LocalAutomationActionType.Move:
            case LocalAutomationActionType.Rename:
                File.Move(source!, destination!, action.Overwrite);
                AddResult(result, action.Type, destination, $"Moved '{source}' to '{destination}'.");
                break;

            case LocalAutomationActionType.Delete:
                DeletePath(path!);
                AddResult(result, action.Type, path, $"Deleted '{path}'.");
                break;

            case LocalAutomationActionType.WriteTextFile:
                WriteTextFile(path!, action);
                AddResult(result, action.Type, path, $"Wrote text file '{path}'.");
                break;

            case LocalAutomationActionType.Zip:
                ZipPath(source!, destination!, action.Overwrite);
                AddResult(result, action.Type, destination, $"Created zip '{destination}'.");
                break;

            case LocalAutomationActionType.Unzip:
                UnzipPath(source!, destination!, action.Overwrite);
                AddResult(result, action.Type, destination, $"Extracted '{source}' into '{destination}'.");
                break;

            case LocalAutomationActionType.RunPowerShell:
                RunPowerShell(path, action, result, cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"Unsupported local automation action type '{action.Type}'.");
        }
    }

    private static void DeletePath(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        throw new FileNotFoundException($"Path '{path}' was not found.", path);
    }

    private static void WriteTextFile(string path, LocalAutomationAction action)
    {
        var targetDirectory = Path.GetDirectoryName(path);
        if (action.CreateParents && !string.IsNullOrWhiteSpace(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        File.WriteAllText(path, action.Content ?? string.Empty);
    }

    private static void ZipPath(string source, string destination, bool overwrite)
    {
        if (File.Exists(destination))
        {
            if (!overwrite)
                throw new IOException($"Destination '{destination}' already exists.");

            File.Delete(destination);
        }

        if (Directory.Exists(source))
        {
            ZipFile.CreateFromDirectory(source, destination);
            return;
        }

        if (File.Exists(source))
        {
            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(source, Path.GetFileName(source));
            return;
        }

        throw new FileNotFoundException($"Source '{source}' was not found.", source);
    }

    private static void UnzipPath(string source, string destination, bool overwrite)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException($"Source '{source}' was not found.", source);

        if (Directory.Exists(destination))
        {
            if (!overwrite)
                throw new IOException($"Destination '{destination}' already exists.");

            Directory.Delete(destination, recursive: true);
        }

        ZipFile.ExtractToDirectory(source, destination);
    }

    private static void AddResult(LocalAutomationExecutionResult result, LocalAutomationActionType type, string? path, string message)
    {
        if (!string.IsNullOrWhiteSpace(path))
            result.TouchedPaths.Add(path);

        result.ActionResults.Add(new LocalAutomationActionResult
        {
            Type = type,
            Success = true,
            Message = message,
            Path = path
        });
    }

    private static void RunPowerShell(string? workingDirectory, LocalAutomationAction action, LocalAutomationExecutionResult result, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{EscapePowerShell(action.Script!)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);

        var stdout = (stdoutTask.Result ?? string.Empty).Trim();
        var stderr = (stderrTask.Result ?? string.Empty).Trim();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"PowerShell exited with code {process.ExitCode}."
                : stderr);

        var message = string.IsNullOrWhiteSpace(stdout)
            ? "PowerShell completed successfully."
            : $"PowerShell output: {stdout}";

        AddResult(result, action.Type, workingDirectory, message);
        result.Trace = string.Join(Environment.NewLine, new[] { result.Trace, stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string EscapePowerShell(string script)
        => script.Replace("\"", "`\"");
}
