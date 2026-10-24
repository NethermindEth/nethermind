#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include ScriptProcess.Helpers.cs

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var projects = (Environment.GetEnvironmentVariable("PROJECTS") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (projects.Length == 0)
        throw new ArgumentException("PROJECTS lists the test projects to run");
    var failed = new List<string>();
    // Timing-sensitive tests must not compete with another test host for CPU.
    foreach (var project in projects)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        Console.WriteLine($"::group::{project}");
        try
        {
            var directory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "Nethermind", project));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Project directory does not exist: {directory}");
            if (!await RunProject(directory, project, cancellation.Token))
                failed.Add(project);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"::error::{project}: {exception.Message}");
            failed.Add(project);
        }
        finally
        {
            Console.WriteLine("::endgroup::");
        }
    }
    if (failed.Count == 0)
        return 0;
    Console.WriteLine($"::error::Failed: {string.Join(' ', failed)}");
    return 1;
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<bool> RunProject(string directory, string project, CancellationToken cancellationToken)
{
    var linux = Environment.GetEnvironmentVariable("RUNNER_OS") == "Linux";
    // Analyzers already run in build-solutions.
    if (!linux && await Execute(directory, "dotnet", ["build", $"{project}.csproj", "-c", "release", "-p:RunAnalyzersDuringBuild=false"], cancellationToken) != 0)
        return false;
    // macOS build servers can fail to shut down; this must not fail the test pack.
    await Execute(directory, "dotnet", ["build-server", "shutdown"], cancellationToken);
    var command = linux ? Path.GetFullPath(Path.Combine(directory, "..", "artifacts", "bin", project, "release", project)) : "dotnet";
    var arguments = new List<string>();
    if (linux)
        arguments.AddRange(["--results-directory", "TestResults"]);
    else
        arguments.AddRange(["test", "--project", $"{project}.csproj", "-c", "release", "--no-build", "--no-restore"]);
    // The in-job build on Windows/macOS leaves less time for hangs.
    arguments.AddRange(["--hangdump", "--hangdump-timeout", linux ? "8m" : "4m", "--hangdump-type", "Mini",
        "--crashdump", "--crashdump-type", "Mini", "--crash-report-if-supported"]);
    var coverage = Environment.GetEnvironmentVariable("COVERAGE") == "true";
    // The coverage controller restarts the host and requires an absolute settings path.
    if (coverage)
        arguments.AddRange(["--coverage", "--coverage-output-format", "cobertura", "--coverage-settings",
            Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "", "src/Nethermind/codecoverage.json")]);
    var result = await Execute(directory, command, arguments.ToArray(), cancellationToken);
    // Retrying a reported test failure would mask it; retry only process crashes.
    if (result is 1 or 2)
        return false;
    if (result != 0)
    {
        if (HasResult(directory, "*_hang.*"))
        {
            Console.WriteLine($"::error::{project}: test host hung and was dumped; see the hang-dump artifact");
            return false;
        }
        if (Environment.GetEnvironmentVariable("CRASH_RETRY") == "false")
        {
            Console.WriteLine($"::error::{project}: test process crashed (exit code {result})");
            return false;
        }
        Console.WriteLine($"::warning::{project}: test process crashed (exit code {result}), retrying...");
        var githubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (string.IsNullOrEmpty(githubOutput))
        {
            Console.WriteLine($"::error::{project}: GITHUB_OUTPUT is required for crash retries");
            return false;
        }
        // Retain the crash dump even if the retry succeeds.
        File.AppendAllText(githubOutput, "crashed=true\n");
        if (await Execute(directory, command, arguments.ToArray(), cancellationToken) != 0)
            return false;
    }
    if (coverage && !HasResult(directory, "*.cobertura.xml"))
    {
        Console.WriteLine($"::error::{project}: wrote no coverage report");
        return false;
    }
    return true;
}

static bool HasResult(string directory, string pattern) =>
    Directory.Exists(Path.Combine(directory, "TestResults")) &&
    Directory.EnumerateFiles(Path.Combine(directory, "TestResults"), pattern).Any();

static async Task<int> Execute(string directory, string command, string[] arguments, CancellationToken cancellationToken) =>
    (await ScriptProcess.Run(command, arguments, cancellationToken, echo: true, workingDirectory: directory, capture: false)).Code;
