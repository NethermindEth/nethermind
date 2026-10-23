#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include BenchmarkProcess.Helpers.cs
#:include BenchmarkState.Helpers.cs
#:include BenchmarkProfiling.Helpers.cs

using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    var directory = BenchmarkProcess.EnvironmentValue("STATE_DIR");
    if (directory.Length == 0) throw new ArgumentException("STATE_DIR: directory where StartNode persisted state");
    var path = BenchmarkProcess.EnvironmentValue("NODE_ENV_FILE", Path.Combine(directory, "node.env"));
    if (!File.Exists(path)) throw new IOException($"no {Path.GetFileName(path)} in {directory} (node never started?)");
    var state = BenchmarkState.Read(path);
    string Get(string name, string fallback = "") => state.GetValueOrDefault(name, BenchmarkProcess.EnvironmentValue(name, fallback));
    var container = Get("CONTAINER_NAME");
    if (Get("PROFILE_AFTER_WARMUP", "false") != "true") throw new IOException($"node {container} was started without PROFILE_AFTER_WARMUP=true — its profilers already run since RPC became ready");
    var profiler = new BenchmarkProfiling(state);
    if (Get("PERF", "false") == "true") await profiler.RequirePerfAsync(cancellation.Token);
    var listing = await BenchmarkProcess.RunAsync("docker", ["ps", "--format", "{{.Names}}"], cancellation.Token);
    if (listing.Code != 0 || !listing.Output.Split('\n').Select(line => line.TrimEnd('\r')).Contains(container, StringComparer.Ordinal)) throw new IOException($"container '{container}' is not running — nothing to profile");
    BenchmarkProcess.Log($"=== Starting profilers after the warm-up (perf: {Get("PERF", "false")}, dotTrace: {Get("DOTTRACE", "false")}, dotnet-trace: {Get("DOTNET_TRACE", "false")}) ===");
    await profiler.StartAsync(path, cancellation.Token);
    BenchmarkProcess.Log("=== Profilers running; the measured phase may begin ===");
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Profiler startup cancelled."); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}"); return 1;
}
finally { Console.CancelKeyPress -= cancel; }
