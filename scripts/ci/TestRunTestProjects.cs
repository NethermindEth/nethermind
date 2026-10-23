#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include RunTestProjects.Helpers.cs

using System.Diagnostics;
using System.IO.Pipes;

if (args.Length > 0 && args[0] == "--child")
{
    using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.Out, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(CancellationToken.None);
    using var writer = new StreamWriter(pipe) { AutoFlush = true };
    await writer.WriteLineAsync(Environment.ProcessId.ToString());
    await writer.WriteLineAsync(Directory.GetCurrentDirectory());
    await writer.WriteLineAsync(args[2]);
    if (args[3] == "wait")
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    return int.Parse(args[3]);
}

var passed = 0;
foreach (var (name, os, firstExit, secondExit, retry, hang, coverage, report, buildFails, expected) in new[]
{
    ("Linux prebuilt host", "Linux", 0, 0, true, false, false, false, false, 0),
    ("Windows build then test", "Windows", 0, 0, true, false, false, false, false, 0),
    ("macOS build failure", "macOS", 0, 0, true, false, false, false, true, 1),
    ("exit 1 no retry", "Linux", 1, 0, true, false, false, false, false, 1),
    ("assertion failure no retry", "Linux", 2, 0, true, false, false, false, false, 1),
    ("crash retry succeeds", "Linux", 7, 0, true, false, false, false, false, 0),
    ("crash retry fails", "Linux", 7, 7, true, false, false, false, false, 1),
    ("retry disabled", "Linux", 7, 0, false, false, false, false, false, 1),
    ("hang dump prevents retry", "Linux", 7, 0, true, true, false, false, false, 1),
    ("missing coverage", "Linux", 0, 0, true, false, true, false, false, 1),
    ("coverage written", "Linux", 0, 0, true, false, true, true, false, 0)
})
{
    var root = Directory.CreateTempSubdirectory("test project runner ");
    try
    {
        var environment = new Dictionary<string, string>
        {
            ["PROJECTS"] = "First Second", ["RUNNER_OS"] = os, ["CRASH_RETRY"] = retry ? "true" : "false",
            ["COVERAGE"] = coverage ? "true" : "false", ["GITHUB_WORKSPACE"] = root.FullName,
            ["GITHUB_OUTPUT"] = Path.Combine(root.FullName, "output")
        };
        foreach (var project in new[] { "First", "Second" })
            Directory.CreateDirectory(Path.Combine(root.FullName, "src/Nethermind", project, "TestResults"));
        var calls = new List<(string Directory, string Command, string[] Args)>();
        var attempts = 0;
        using var output = new StringWriter();
        var result = await TestProjects.Run(root.FullName, key => environment.GetValueOrDefault(key),
            (directory, command, arguments, token) =>
            {
                calls.Add((directory, command, arguments));
                if (arguments[0] == "build")
                    return Task.FromResult(buildFails && directory.EndsWith("First") ? 1 : 0);
                if (arguments[0] == "build-server")
                    return Task.FromResult(5);
                if (directory.EndsWith("Second"))
                {
                    if (report)
                        File.WriteAllText(Path.Combine(directory, "TestResults/report.cobertura.xml"), "");
                    return Task.FromResult(0);
                }
                attempts++;
                if (hang)
                    File.WriteAllText(Path.Combine(directory, "TestResults/test_hang.dmp"), "");
                if (report)
                    File.WriteAllText(Path.Combine(directory, "TestResults/report.cobertura.xml"), "");
                return Task.FromResult(attempts == 1 ? firstExit : secondExit);
            }, output, CancellationToken.None);
        var retried = !buildFails && firstExit is not (0 or 1 or 2) && retry && !hang;
        if (result != expected || attempts != (buildFails ? 0 : retried ? 2 : 1) || !calls.Any(call => call.Directory.EndsWith("Second")))
            throw new Exception($"{name}: result/attempt count/continue-after-failure mismatch");
        if (File.Exists(environment["GITHUB_OUTPUT"]) != retried)
            throw new Exception($"{name}: crash marker mismatch");
        foreach (var call in calls.Where(call => call.Args[0] is not ("build" or "build-server")))
        {
            if (!call.Args.Contains(os == "Linux" ? "8m" : "4m") || call.Command != (os == "Linux" ? $"../artifacts/bin/{Path.GetFileName(call.Directory)}/release/{Path.GetFileName(call.Directory)}" : "dotnet"))
                throw new Exception($"{name}: command/timeout mismatch");
            if (coverage && !call.Args.Contains(Path.Combine(root.FullName, "src/Nethermind/codecoverage.json")))
                throw new Exception($"{name}: coverage path is not absolute");
        }
        Console.WriteLine($"PASS: {name}");
        passed++;
    }
    finally
    {
        root.Delete(recursive: true);
    }
}

foreach (var cancel in new[] { false, true })
{
    var root = Directory.CreateTempSubdirectory("real test process ");
    try
    {
        var pipeName = $"test-project-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var execution = TestProjects.Execute(root.FullName, Environment.ProcessPath!,
            ["--child", pipeName, "argument with spaces", cancel ? "wait" : "7"], cancellation.Token);
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(pipe);
        var pid = int.Parse((await reader.ReadLineAsync(timeout.Token))!);
        var cwd = await reader.ReadLineAsync(timeout.Token);
        var argument = await reader.ReadLineAsync(timeout.Token);
        if (cwd != root.FullName || argument != "argument with spaces")
            throw new Exception("Real child lost its working directory or argument boundary.");
        if (cancel)
        {
            cancellation.Cancel();
            try
            {
                await execution;
                throw new Exception("Cancellation must abort execution.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            try
            {
                using var child = Process.GetProcessById(pid);
                if (!child.HasExited)
                    throw new Exception("Cancelled child is still running.");
            }
            catch (ArgumentException)
            {
                // The process can already have been reaped after WaitForExitAsync.
            }
        }
        else if (await execution != 7)
            throw new Exception("Real child exit code was not propagated.");
        Console.WriteLine($"PASS: real child arguments, working directory and {(cancel ? "cancellation" : "exit code")}");
        passed++;
    }
    finally
    {
        root.Delete(recursive: true);
    }
}

var cancelledRoot = Directory.CreateTempSubdirectory("cancelled projects ");
try
{
    foreach (var name in new[] { "First", "Second" })
        Directory.CreateDirectory(Path.Combine(cancelledRoot.FullName, "src/Nethermind", name));
    using var cancellation = new CancellationTokenSource();
    var secondRan = false;
    try
    {
        await TestProjects.Run(cancelledRoot.FullName, key => key switch { "PROJECTS" => "First Second", "RUNNER_OS" => "Linux", _ => null },
            (directory, command, arguments, token) =>
            {
                secondRan |= directory.EndsWith("Second");
                cancellation.Cancel();
                return Task.FromCanceled<int>(token);
            }, TextWriter.Null, cancellation.Token);
        throw new Exception("Cancellation must stop the project sequence.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        if (secondRan)
            throw new Exception("Second project ran after cancellation.");
    }
    Console.WriteLine("PASS: cancellation prevents subsequent projects");
    passed++;
}
finally
{
    cancelledRoot.Delete(recursive: true);
}
Console.WriteLine($"Passed {passed} tests.");
return 0;
