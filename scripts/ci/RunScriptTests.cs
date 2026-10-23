#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.RegularExpressions;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[0] != "--check"))
{
    Console.Error.WriteLine("Usage: dotnet run --file scripts/ci/RunScriptTests.cs -- <suite-directory> | --check <file-or-directory>");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    var check = args.Length == 2;
    var path = Path.GetFullPath(args[^1]);
    if (check)
    {
        var files = File.Exists(path)
            ? [path]
            : Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
                .Where(file => char.IsUpper(Path.GetFileName(file)[0]) &&
                    !file.EndsWith(".Helpers.cs", StringComparison.Ordinal) &&
                    !Path.GetRelativePath(path, file).Split(Path.DirectorySeparatorChar).Any(part => part is "build" or "bin" or "obj"))
                .Order(StringComparer.Ordinal).ToArray();
        var artifacts = Directory.CreateTempSubdirectory("nethermind-script-check-");
        try
        {
            foreach (var file in files)
                await Run("dotnet", ["build", file, "--no-incremental", "--artifacts-path", artifacts.FullName,
                    "-p:EnforceCodeStyleInBuild=true", "-p:GenerateDocumentationFile=true", "-p:WarningsAsErrors=IDE0005"], cancellation.Token);
        }
        finally
        {
            artifacts.Delete(recursive: true);
        }

        Console.WriteLine($"Validated {files.Length} C# entry point(s), including their helpers.");
        return 0;
    }

    var pythonFiles = Directory.GetFiles(path, "test*.py").Order(StringComparer.Ordinal).ToArray();
    var csharpFiles = Directory.GetFiles(path, "Test*.cs")
        .Where(file => !file.EndsWith(".Helpers.cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
    var shellTest = Path.Combine(path, "test-fetch-artifacts.sh");
    var runShellTest = !File.Exists(Path.Combine(path, "TestFetchArtifacts.cs")) && File.Exists(shellTest);
    if (pythonFiles.Length + csharpFiles.Length == 0 && !runShellTest)
        throw new InvalidOperationException($"No test suites found in {path}.");

    Console.WriteLine($"Discovered {pythonFiles.Length} Python test file(s), {csharpFiles.Length} C# suite(s), {(runShellTest ? 1 : 0)} shell suite(s).");
    if (pythonFiles.Length > 0)
    {
        var output = await Run(OperatingSystem.IsWindows() ? "python" : "python3",
            ["-m", "unittest", "discover", "-s", path, "-p", "test*.py", "-v"], cancellation.Token);
        if (!Regex.IsMatch(output, @"\bRan [1-9][0-9]* tests?\b"))
            throw new InvalidOperationException("Python discovery did not report any executed tests.");
    }

    foreach (var file in csharpFiles)
    {
        var output = await Run("dotnet", ["run", "--file", file], cancellation.Token);
        if (!Regex.IsMatch(output, @"(?m)^Passed [1-9][0-9]* tests?\.\r?$"))
            throw new InvalidOperationException($"{file} did not report a positive 'Passed N tests.' summary.");
    }
    if (runShellTest)
        await Run("bash", [shellTest], cancellation.Token);

    Console.WriteLine($"Passed {pythonFiles.Length + csharpFiles.Length + (runShellTest ? 1 : 0)} test file(s).");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Script validation cancelled.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<string> Run(string executable, string[] arguments, CancellationToken cancellationToken)
{
    Console.WriteLine($"> {executable} {string.Join(' ', arguments)}");
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }
    };
    foreach (var argument in arguments)
        process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var stdout = Drain(process.StandardOutput, Console.Out, cancellationToken);
    var stderr = Drain(process.StandardError, Console.Error, cancellationToken);
    try
    {
        await process.WaitForExitAsync(cancellationToken);
        var output = string.Join('\n', await Task.WhenAll(stdout, stderr));
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} exited with code {process.ExitCode}.");
        return output;
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(stdout, stderr);
    }
}

static async Task<string> Drain(StreamReader reader, TextWriter writer, CancellationToken cancellationToken)
{
    var output = new System.Text.StringBuilder();
    while (await reader.ReadLineAsync(cancellationToken) is { } line)
    {
        await writer.WriteLineAsync(line);
        output.AppendLine(line);
    }
    return output.ToString();
}
