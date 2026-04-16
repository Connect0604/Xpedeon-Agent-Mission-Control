using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public sealed class LocalCapabilityExecutor
{
    private readonly LocalAutomationExecutor _localAutomationExecutor;
    private readonly LocalAutomationValidator? _localAutomationValidator;

    public LocalCapabilityExecutor(LocalAutomationExecutor localAutomationExecutor, LocalAutomationValidator? localAutomationValidator = null)
    {
        _localAutomationExecutor = localAutomationExecutor;
        _localAutomationValidator = localAutomationValidator;
    }

    public async Task<LocalCapabilityExecutionResult> ExecuteAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!capability.IsActive)
        {
            throw new InvalidOperationException($"Capability '{capability.Name}' is not active.");
        }

        if (capability.ExecutionType == LocalCapabilityExecutionType.PowerShell)
        {
            return await ExecutePowerShellAsync(capability, inputs, cancellationToken);
        }

        if (capability.ExecutionType != LocalCapabilityExecutionType.BuiltIn)
        {
            throw new NotSupportedException($"Capability '{capability.Name}' uses unsupported execution type '{capability.ExecutionType}'.");
        }

        var handlerKey = capability.HandlerKey ?? capability.Name;
        return await ExecuteBuiltInAsync(capability, handlerKey, inputs, cancellationToken);
    }

    private Task<LocalCapabilityExecutionResult> ExecuteBuiltInAsync(
        LocalCapability capability,
        string handlerKey,
        IReadOnlyDictionary<string, string?> inputs,
        CancellationToken cancellationToken)
    {
        return handlerKey switch
        {
            "countFiles" => Task.FromResult(ExecuteCountFilesAsync(capability, inputs)),
            "listFiles" => Task.FromResult(ExecuteListFilesAsync(capability, inputs)),
            "getFileMetadata" => Task.FromResult(ExecuteGetFileMetadataAsync(capability, inputs)),
            "readTextFile" => Task.FromResult(ExecuteReadTextFileAsync(capability, inputs)),
            "createDirectory" => ExecuteCreateDirectoryAsync(capability, inputs, cancellationToken),
            "writeTextFile" => ExecuteWriteTextFileAsync(capability, inputs, cancellationToken),
            "copy" => ExecuteCopyMoveRenameAsync(capability, inputs, LocalAutomationActionType.Copy, cancellationToken),
            "move" => ExecuteCopyMoveRenameAsync(capability, inputs, LocalAutomationActionType.Move, cancellationToken),
            "rename" => ExecuteCopyMoveRenameAsync(capability, inputs, LocalAutomationActionType.Rename, cancellationToken),
            _ => Task.FromException<LocalCapabilityExecutionResult>(new InvalidOperationException($"Unsupported built-in capability handler '{handlerKey}'."))
        };
    }

    private LocalCapabilityExecutionResult ExecuteCountFilesAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs)
    {
        var rootPath = RequireInput(inputs, "rootPath");
        ValidateCandidatePaths(capability, rootPath);
        var pattern = string.IsNullOrWhiteSpace(GetInput(inputs, "pattern")) ? "*.*" : GetInput(inputs, "pattern")!;
        var recursive = ParseBoolean(GetInput(inputs, "recursive"));
        var normalizedRoot = LocalAutomationPathResolver.NormalizePath(rootPath);

        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Root path '{normalizedRoot}' was not found.");
        }

        var files = Directory.GetFiles(
            normalizedRoot,
            pattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

        return new LocalCapabilityExecutionResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(new { count = files.Length }),
            Trace = $"countFiles matched {files.Length} file(s).",
            Message = $"Counted {files.Length} file(s)."
        };
    }

    private LocalCapabilityExecutionResult ExecuteListFilesAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs)
    {
        var rootPath = RequireInput(inputs, "rootPath");
        ValidateCandidatePaths(capability, rootPath);
        var pattern = string.IsNullOrWhiteSpace(GetInput(inputs, "pattern")) ? "*.*" : GetInput(inputs, "pattern")!;
        var recursive = ParseBoolean(GetInput(inputs, "recursive"));
        var normalizedRoot = LocalAutomationPathResolver.NormalizePath(rootPath);

        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Root path '{normalizedRoot}' was not found.");
        }

        var files = Directory.GetFiles(
            normalizedRoot,
            pattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

        return new LocalCapabilityExecutionResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(new { files }),
            Trace = $"listFiles matched {files.Length} file(s).",
            Message = $"Listed {files.Length} file(s)."
        };
    }

    private LocalCapabilityExecutionResult ExecuteGetFileMetadataAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs)
    {
        var path = RequireInput(inputs, "path");
        ValidateCandidatePaths(capability, path);
        var normalizedPath = LocalAutomationPathResolver.NormalizePath(path);

        if (File.Exists(normalizedPath))
        {
            var info = new FileInfo(normalizedPath);
            return BuildMetadataResult(normalizedPath, true, false, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
        }

        if (Directory.Exists(normalizedPath))
        {
            var info = new DirectoryInfo(normalizedPath);
            return BuildMetadataResult(normalizedPath, true, true, 0, info.CreationTimeUtc, info.LastWriteTimeUtc);
        }

        return BuildMetadataResult(normalizedPath, false, false, 0, null, null);
    }

    private LocalCapabilityExecutionResult ExecuteReadTextFileAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs)
    {
        var path = RequireInput(inputs, "path");
        ValidateCandidatePaths(capability, path);
        var normalizedPath = LocalAutomationPathResolver.NormalizePath(path);

        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException($"File '{normalizedPath}' was not found.", normalizedPath);
        }

        return new LocalCapabilityExecutionResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(new { content = File.ReadAllText(normalizedPath) }),
            Trace = $"readTextFile read '{normalizedPath}'.",
            Message = $"Read text file '{normalizedPath}'."
        };
    }

    private async Task<LocalCapabilityExecutionResult> ExecuteCreateDirectoryAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs,
        CancellationToken cancellationToken)
    {
        var path = RequireInput(inputs, "path");
        ValidateCandidatePaths(capability, path);
        var normalizedPath = LocalAutomationPathResolver.NormalizePath(path);

        var result = await ExecuteLocalAutomationAsync(new LocalAutomationAction
        {
            Type = LocalAutomationActionType.CreateDirectory,
            Path = normalizedPath
        }, cancellationToken);

        return new LocalCapabilityExecutionResult
        {
            Success = result.Success,
            Output = JsonSerializer.Serialize(new { path = normalizedPath }),
            Trace = result.Trace,
            Message = result.Message
        };
    }

    private async Task<LocalCapabilityExecutionResult> ExecuteWriteTextFileAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs,
        CancellationToken cancellationToken)
    {
        var path = RequireInput(inputs, "path");
        var resolvedPath = ResolvePathForCapability(capability, path);
        ValidateCandidatePaths(capability, resolvedPath);
        var normalizedPath = LocalAutomationPathResolver.NormalizePath(resolvedPath);

        var result = await ExecuteLocalAutomationAsync(new LocalAutomationAction
        {
            Type = LocalAutomationActionType.WriteTextFile,
            Path = normalizedPath,
            Content = GetInput(inputs, "content") ?? string.Empty,
            CreateParents = ParseBoolean(GetInput(inputs, "createParents"))
        }, cancellationToken);

        return new LocalCapabilityExecutionResult
        {
            Success = result.Success,
            Output = JsonSerializer.Serialize(new { path = normalizedPath }),
            Trace = result.Trace,
            Message = result.Message
        };
    }

    private async Task<LocalCapabilityExecutionResult> ExecuteCopyMoveRenameAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs,
        LocalAutomationActionType actionType,
        CancellationToken cancellationToken)
    {
        var source = RequireInput(inputs, "source");
        var destination = RequireInput(inputs, "destination");
        ValidateCandidatePaths(capability, source, destination);

        var result = await ExecuteLocalAutomationAsync(new LocalAutomationAction
        {
            Type = actionType,
            Source = LocalAutomationPathResolver.NormalizePath(source),
            Destination = LocalAutomationPathResolver.NormalizePath(destination),
            Overwrite = ParseBoolean(GetInput(inputs, "overwrite"))
        }, cancellationToken);

        return new LocalCapabilityExecutionResult
        {
            Success = result.Success,
            Output = JsonSerializer.Serialize(new { destination = LocalAutomationPathResolver.NormalizePath(destination) }),
            Trace = result.Trace,
            Message = result.Message
        };
    }

    private async Task<LocalAutomationExecutionResult> ExecuteLocalAutomationAsync(LocalAutomationAction action, CancellationToken cancellationToken)
    {
        var plan = new LocalAutomationPlan
        {
            Summary = action.Type.ToString(),
            Actions = [action]
        };

        return await _localAutomationExecutor.ExecuteAsync(plan, cancellationToken);
    }

    private static async Task<LocalCapabilityExecutionResult> ExecutePowerShellAsync(
        LocalCapability capability,
        IReadOnlyDictionary<string, string?> inputs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capability.ScriptPath) || !File.Exists(capability.ScriptPath))
        {
            throw new FileNotFoundException($"Capability script '{capability.ScriptPath}' was not found.", capability.ScriptPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(capability.ScriptPath) ?? AppContext.BaseDirectory
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        var inputTypes = ParseInputTypes(capability.InputSchemaJson);
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(BuildPowerShellCommand(capability.ScriptPath, inputs, inputTypes));

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cancellation.
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"PowerShell exited with code {process.ExitCode}."
                : stderr);
        }

        return new LocalCapabilityExecutionResult
        {
            Success = true,
            Output = stdout,
            Trace = string.Join(Environment.NewLine, new[] { $"Executed PowerShell capability '{capability.Name}'.", stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x))),
            Message = $"Executed capability '{capability.Name}'."
        };
    }

    private LocalCapabilityExecutionResult BuildMetadataResult(
        string normalizedPath,
        bool exists,
        bool isDirectory,
        long sizeBytes,
        DateTime? createdUtc,
        DateTime? modifiedUtc)
    {
        return new LocalCapabilityExecutionResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(new
            {
                path = normalizedPath,
                exists,
                isDirectory,
                sizeBytes,
                createdUtc,
                modifiedUtc
            }),
            Trace = $"getFileMetadata inspected '{normalizedPath}'.",
            Message = $"Read metadata for '{normalizedPath}'."
        };
    }

    private void ValidateCandidatePaths(LocalCapability capability, params string[] candidatePaths)
    {
        if (_localAutomationValidator is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(capability.AllowedRootsJson))
        {
            return;
        }

        _localAutomationValidator.ValidateCapabilityInputPaths(capability.AllowedRootsJson, candidatePaths);
    }

    private string ResolvePathForCapability(LocalCapability capability, string candidatePath)
    {
        if (_localAutomationValidator is null || string.IsNullOrWhiteSpace(capability.AllowedRootsJson))
        {
            return candidatePath;
        }

        return _localAutomationValidator.ResolvePathAgainstAllowedRoots(capability.AllowedRootsJson, candidatePath);
    }

    private static string RequireInput(IReadOnlyDictionary<string, string?> inputs, string key)
    {
        var value = GetInput(inputs, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Capability input '{key}' is required.");
        }

        return value;
    }

    private static string? GetInput(IReadOnlyDictionary<string, string?> inputs, string key)
        => inputs.TryGetValue(key, out var value) ? value : null;

    private static bool ParseBoolean(string? value)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseInputTypes(string? inputSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(inputSchemaJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var schema = JsonNode.Parse(inputSchemaJson)?.AsObject();
            if (schema is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in schema)
            {
                if (kvp.Value is null)
                {
                    continue;
                }

                var typeName = kvp.Value.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    result[kvp.Key] = typeName;
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsBooleanInput(IReadOnlyDictionary<string, string> inputTypes, string inputName)
        => inputTypes.TryGetValue(inputName, out var typeName)
           && string.Equals(typeName, "boolean", StringComparison.OrdinalIgnoreCase);

    private static string BuildPowerShellCommand(
        string scriptPath,
        IReadOnlyDictionary<string, string?> inputs,
        IReadOnlyDictionary<string, string> inputTypes)
    {
        var segments = new List<string> { $"& '{EscapePowerShellSingleQuoted(scriptPath)}'" };

        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Key) || input.Value is null)
            {
                continue;
            }

            if (IsBooleanInput(inputTypes, input.Key))
            {
                segments.Add($"-{input.Key}:${(ParseBoolean(input.Value) ? "true" : "false")}");
                continue;
            }

            segments.Add($"-{input.Key} '{EscapePowerShellSingleQuoted(input.Value)}'");
        }

        return string.Join(" ", segments);
    }

    private static string EscapePowerShellSingleQuoted(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
