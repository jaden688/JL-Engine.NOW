using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace JLEngine.Runtime.Tools;

/// <summary>Port of Tools.jl's `_run_shell_capture` — cross-platform shell
/// execution (PowerShell on Windows via a temp .ps1 file, bash/sh elsewhere),
/// capturing combined stdout/stderr.</summary>
public static class ShellRunner
{
    public static async Task<(bool Ok, string Output, string? Error)> RunAsync(string command)
    {
        string fileName;
        string arguments;
        string? tempFile = null;

        if (OperatingSystem.IsWindows())
        {
            tempFile = Path.GetTempFileName() + ".ps1";
            await File.WriteAllTextAsync(tempFile, command);
            fileName = "powershell";
            arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"{tempFile}\"";
        }
        else
        {
            fileName = "/bin/bash";
            arguments = $"-lc \"{command.Replace("\"", "\\\"")}\"";
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var combined = stdout + stderr;
            return process.ExitCode == 0 ? (true, combined, null) : (false, combined, $"exit code {process.ExitCode}");
        }
        catch (Exception e)
        {
            return (false, "", e.Message);
        }
        finally
        {
            if (tempFile is not null) File.Delete(tempFile);
        }
    }
}

/// <summary>Port of tool_run_command. Includes the self-destruct guard: the
/// engine runs inside this process, so killing this process kills the
/// engine, not a separate target — block the obvious self-kill shapes.</summary>
public sealed partial class RunCommandTool : ITool
{
    public string Name => "run_command";

    [GeneratedRegex(@"Stop-Process\s+.*jl", RegexOptions.IgnoreCase)]
    private static partial Regex StopProcessPattern();
    [GeneratedRegex(@"taskkill\s+.*\bjlengine", RegexOptions.IgnoreCase)]
    private static partial Regex TaskkillPattern();
    [GeneratedRegex(@"\b(kill|pkill|killall)\b.*jlengine", RegexOptions.IgnoreCase)]
    private static partial Regex KillPattern();

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var cmd = ToolArgs.GetArg(args, "command", "cmd", "cmd_str", "shell", "exec");
        if (string.IsNullOrEmpty(cmd))
        {
            return new Dictionary<string, object?> { ["error"] = "Missing required argument: 'command'" };
        }

        if (StopProcessPattern().IsMatch(cmd) || TaskkillPattern().IsMatch(cmd) || KillPattern().IsMatch(cmd))
        {
            return new Dictionary<string, object?>
            {
                ["error"] = "BLOCKED: that command would kill the JL Engine process itself. " +
                    "Use the restart endpoint or ask the operator instead.",
            };
        }

        var (ok, output, error) = await ShellRunner.RunAsync(cmd);
        return ok
            ? new Dictionary<string, object?> { ["result"] = output }
            : new Dictionary<string, object?> { ["error"] = error ?? "Command failed.", ["output"] = output };
    }
}

public sealed class GetOsInfoTool : ITool
{
    public string Name => "get_os_info";

    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args) =>
        Task.FromResult(new Dictionary<string, object?>
        {
            ["os"] = Environment.OSVersion.ToString(),
            ["cpu_architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["dotnet"] = Environment.Version.ToString(),
        });
}

/// <summary>
/// Port of tool_execute_code. Julia dispatches to a Julia or Python
/// interpreter based on the code's shape; the natural C# equivalent runs
/// C# via Roslyn scripting (CSharpScript) in-process, or Python via an
/// external interpreter if one is installed — mirroring the dual-language
/// dispatch with "csharp" replacing "julia" as the native language.
/// </summary>
public sealed class ExecuteCodeTool : ITool
{
    public string Name => "execute_code";

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var code = ToolArgs.GetNamedArg(args, "code", "script", "program", "source");
        var lang = InferLanguage(code, ToolArgs.GetNamedArg(args, "language", "lang"));
        if (string.IsNullOrEmpty(code))
        {
            return new Dictionary<string, object?> { ["error"] = "Missing required argument: 'code'" };
        }

        if (lang == "python")
        {
            return await RunPythonAsync(code);
        }

        if (lang != "csharp")
        {
            return new Dictionary<string, object?> { ["error"] = $"Unsupported language '{lang}'. Use 'csharp' or 'python'." };
        }

        try
        {
            var result = await CSharpScript.EvaluateAsync<object>(code, ScriptDefaults.Options);
            return new Dictionary<string, object?> { ["stdout"] = result?.ToString() ?? "", ["language"] = "csharp" };
        }
        catch (CompilationErrorException e)
        {
            return new Dictionary<string, object?> { ["error"] = string.Join("\n", e.Diagnostics), ["language"] = "csharp" };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = e.Message, ["language"] = "csharp" };
        }
    }

    private static string InferLanguage(string code, string lang)
    {
        var normalized = lang.Trim().ToLowerInvariant();
        if (normalized is "py" or "python" or "python3") return "python";
        if (normalized is "cs" or "csharp" or "c#") return "csharp";
        if (normalized.Length > 0) return normalized;

        string[] pythonMarkers = [@"^\s*(from\s+\w[\w.]*\s+import\s+|import\s+\w[\w.]*)", @"^\s*(def|class)\s+\w+\s*[\(:]", @"\bprint\s*\("];
        return pythonMarkers.Any(m => Regex.IsMatch(code, m, RegexOptions.Multiline)) ? "python" : "csharp";
    }

    private static async Task<Dictionary<string, object?>> RunPythonAsync(string code)
    {
        var tmp = Path.GetTempFileName() + ".py";
        try
        {
            await File.WriteAllTextAsync(tmp, code);
            var python = Environment.GetEnvironmentVariable("SPARKBYTE_PYTHON");
            if (string.IsNullOrEmpty(python)) python = OperatingSystem.IsWindows() ? "py" : "python3";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(python, $"\"{tmp}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new Dictionary<string, object?> { ["stdout"] = stdout, ["language"] = "python" }
                : new Dictionary<string, object?> { ["error"] = stderr, ["output"] = stdout, ["language"] = "python" };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = $"No Python interpreter found or execution failed: {e.Message}", ["language"] = "python" };
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
