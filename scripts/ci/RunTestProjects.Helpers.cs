// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

internal static class TestProjects
{
    internal static async Task<int> Run(string root, Func<string, string?> environment,
        Func<string, string, string[], CancellationToken, Task<int>> execute, TextWriter output, CancellationToken cancellationToken)
    {
        var projects = (environment("PROJECTS") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (projects.Length == 0)
            throw new ArgumentException("PROJECTS lists the test projects to run");
        var failed = new List<string>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine($"::group::{project}");
            try
            {
                var directory = Path.GetFullPath(Path.Combine(root, "src", "Nethermind", project));
                if (!Directory.Exists(directory))
                    throw new DirectoryNotFoundException($"Project directory does not exist: {directory}");
                if (!await RunProject(directory, project, environment, execute, output, cancellationToken))
                    failed.Add(project);
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
            {
                output.WriteLine($"::error::{project}: {exception.Message}");
                failed.Add(project);
            }
            finally
            {
                output.WriteLine("::endgroup::");
            }
        }
        if (failed.Count == 0)
            return 0;
        output.WriteLine($"::error::Failed: {string.Join(' ', failed)}");
        return 1;
    }

    private static async Task<bool> RunProject(string directory, string project, Func<string, string?> environment,
        Func<string, string, string[], CancellationToken, Task<int>> execute, TextWriter output, CancellationToken cancellationToken)
    {
        var linux = environment("RUNNER_OS") == "Linux";
        if (!linux && await execute(directory, "dotnet", ["build", $"{project}.csproj", "-c", "release", "-p:RunAnalyzersDuringBuild=false"], cancellationToken) != 0)
            return false;
        await execute(directory, "dotnet", ["build-server", "shutdown"], cancellationToken);
        var command = linux ? Path.GetFullPath(Path.Combine(directory, "..", "artifacts", "bin", project, "release", project)) : "dotnet";
        var arguments = new List<string>();
        if (linux)
            arguments.AddRange(["--results-directory", "TestResults"]);
        else
            arguments.AddRange(["test", "--project", $"{project}.csproj", "-c", "release", "--no-build", "--no-restore"]);
        arguments.AddRange(["--hangdump", "--hangdump-timeout", linux ? "8m" : "4m", "--hangdump-type", "Mini",
            "--crashdump", "--crashdump-type", "Mini", "--crash-report-if-supported"]);
        var coverage = environment("COVERAGE") == "true";
        if (coverage)
            arguments.AddRange(["--coverage", "--coverage-output-format", "cobertura", "--coverage-settings",
                Path.Combine(environment("GITHUB_WORKSPACE") ?? "", "src/Nethermind/codecoverage.json")]);
        var result = await execute(directory, command, arguments.ToArray(), cancellationToken);
        if (result is 1 or 2)
            return false;
        if (result != 0)
        {
            if (HasResult(directory, "*_hang.*"))
            {
                output.WriteLine($"::error::{project}: test host hung and was dumped; see the hang-dump artifact");
                return false;
            }
            if (environment("CRASH_RETRY") == "false")
            {
                output.WriteLine($"::error::{project}: test process crashed (exit code {result})");
                return false;
            }
            output.WriteLine($"::warning::{project}: test process crashed (exit code {result}), retrying...");
            var githubOutput = environment("GITHUB_OUTPUT");
            if (string.IsNullOrEmpty(githubOutput))
            {
                output.WriteLine($"::error::{project}: GITHUB_OUTPUT is required for crash retries");
                return false;
            }
            File.AppendAllText(githubOutput, "crashed=true\n");
            if (await execute(directory, command, arguments.ToArray(), cancellationToken) != 0)
                return false;
        }
        if (coverage && !HasResult(directory, "*.cobertura.xml"))
        {
            output.WriteLine($"::error::{project}: wrote no coverage report");
            return false;
        }
        return true;
    }

    private static bool HasResult(string directory, string pattern) =>
        Directory.Exists(Path.Combine(directory, "TestResults")) &&
        Directory.EnumerateFiles(Path.Combine(directory, "TestResults"), pattern).Any();

    internal static async Task<int> Execute(string directory, string command, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command) { WorkingDirectory = directory, UseShellExecute = false }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
