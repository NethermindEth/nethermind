// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;

namespace JitAsm;

internal sealed class JitRunner(string assemblyPath, string? typeName, string methodName, string? typeParams, string? classTypeParams, bool verbose)
{
    public async Task<JitResult> RunSinglePassAsync(IReadOnlyList<string>? cctorsToInit = null)
    {
        var result = await RunJitProcessAsync(cctorsToInit);
        return result;
    }

    public async Task<JitResult> RunTwoPassAsync()
    {
        // Pass 1: Run without cctor initialization to detect static constructor calls
        var pass1Result = await RunJitProcessAsync(null);

        if (!pass1Result.Success)
        {
            return pass1Result;
        }

        // Detect static constructors in the output
        var detectedCctors = StaticCtorDetector.DetectStaticCtors(pass1Result.Output ?? string.Empty);

        if (detectedCctors.Count == 0)
        {
            // No cctors detected, return pass 1 result
            return pass1Result;
        }

        // Pass 2: Run with cctor initialization
        var pass2Result = await RunJitProcessAsync(detectedCctors);

        return new JitResult
        {
            Success = pass2Result.Success,
            Output = pass2Result.Output,
            Error = pass2Result.Error,
            Pass1Output = verbose ? pass1Result.Output : null,
            DetectedCctors = detectedCctors
        };
    }

    private async Task<JitResult> RunJitProcessAsync(IReadOnlyList<string>? cctorsToInit)
    {
        // Get the path to the JitAsm executable
        var executablePath = GetExecutablePath();

        // Build the method pattern for JitDisasm
        var methodPattern = BuildMethodPattern();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Set JIT environment variables - these must be set before the process starts
        startInfo.EnvironmentVariables["DOTNET_TieredCompilation"] = "0";
        startInfo.EnvironmentVariables["DOTNET_TC_QuickJit"] = "0";
        startInfo.EnvironmentVariables["DOTNET_JitDisasm"] = methodPattern;
        startInfo.EnvironmentVariables["DOTNET_JitDiffableDasm"] = "1";

        // Build arguments for internal runner
        var args = new StringBuilder();
        args.Append("--internal-runner ");
        args.Append(EscapeArg(assemblyPath));
        args.Append(' ');
        args.Append(EscapeArg(methodName));

        if (typeName is not null)
        {
            args.Append(" --type ");
            args.Append(EscapeArg(typeName));
        }

        if (typeParams is not null)
        {
            args.Append(" --type-params ");
            args.Append(EscapeArg(typeParams));
        }

        if (classTypeParams is not null)
        {
            args.Append(" --class-type-params ");
            args.Append(EscapeArg(classTypeParams));
        }

        if (cctorsToInit is not null && cctorsToInit.Count > 0)
        {
            args.Append(" --init-cctors ");
            args.Append(EscapeArg(string.Join(";", cctorsToInit)));
        }

        startInfo.Arguments = args.ToString();

        if (verbose)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[grey]Running: {Spectre.Console.Markup.Escape(executablePath)} {Spectre.Console.Markup.Escape(startInfo.Arguments)}[/]");
            Spectre.Console.AnsiConsole.MarkupLine($"[grey]DOTNET_JitDisasm={Spectre.Console.Markup.Escape(methodPattern)}[/]");
            Spectre.Console.AnsiConsole.WriteLine();
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new JitResult { Success = false, Error = "Failed to start process" };
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // Parse the JIT output from stdout (JIT diagnostics can go to either stream)
            var disassembly = DisassemblyParser.Parse(stdout);

            if (string.IsNullOrWhiteSpace(disassembly))
            {
                // Try stderr
                disassembly = DisassemblyParser.Parse(stderr);
            }

            if (string.IsNullOrWhiteSpace(disassembly))
            {
                // Check if there's an error
                if (!string.IsNullOrWhiteSpace(stderr) && stderr.Contains("Error"))
                {
                    return new JitResult { Success = false, Error = stderr.Trim() };
                }

                // No disassembly found
                return new JitResult
                {
                    Success = false,
                    Error = "No disassembly output found. Method may not exist or JIT output was not captured.",
                    Output = $"stdout:\n{stdout}\n\nstderr:\n{stderr}"
                };
            }

            return new JitResult
            {
                Success = true,
                Output = disassembly
            };
        }
        catch (Exception ex)
        {
            return new JitResult { Success = false, Error = ex.Message };
        }
    }

    private string BuildMethodPattern()
    {
        // Build pattern for DOTNET_JitDisasm
        // Just use the method name - the JIT will match any method with this name
        // Type filtering is done post-hoc by parsing the output
        return methodName;
    }

    private static string GetExecutablePath()
    {
        // Get the path to the current executable
        var currentExe = Environment.ProcessPath;
        if (currentExe is not null && File.Exists(currentExe))
        {
            return currentExe;
        }

        // Fallback to assembly location
        var assemblyLocation = typeof(JitRunner).Assembly.Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            // For .dll, try to find the corresponding .exe
            var directory = Path.GetDirectoryName(assemblyLocation)!;
            var exeName = Path.GetFileNameWithoutExtension(assemblyLocation);

            // Try .exe first (Windows)
            var exePath = Path.Combine(directory, exeName + ".exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            // Try without extension (Linux/macOS)
            exePath = Path.Combine(directory, exeName);
            if (File.Exists(exePath))
            {
                return exePath;
            }

            // Use dotnet to run the dll
            return "dotnet";
        }

        throw new InvalidOperationException("Could not determine executable path");
    }

    private static string EscapeArg(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
        {
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        }
        return arg;
    }
}

internal sealed class JitResult
{
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public string? Pass1Output { get; init; }
    public IReadOnlyList<string> DetectedCctors { get; init; } = [];
}
