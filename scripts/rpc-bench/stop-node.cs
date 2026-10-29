#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include benchmark-process.helpers.cs
#:include benchmark-paths.helpers.cs
#:include benchmark-files.helpers.cs
#:include benchmark-state.helpers.cs
#:include benchmark-profiling.helpers.cs
#:include ../profiles.helpers.cs

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    var directory = BenchmarkProcess.EnvironmentValue("STATE_DIR");
    if (directory.Length == 0) throw new ArgumentException("STATE_DIR: directory where StartNode persisted state");
    var statePath = BenchmarkProcess.EnvironmentValue("NODE_ENV_FILE", Path.Combine(directory, "node.env"));
    if (!File.Exists(statePath)) throw new IOException($"no {Path.GetFileName(statePath)} in {directory} (node never started?)");
    var state = BenchmarkState.Read(statePath);
    string Get(string key, string fallback = "") => state.GetValueOrDefault(key, BenchmarkProcess.EnvironmentValue(key, fallback));
    var suffix = Get("INSTANCE_SUFFIX"); var diag = Get("DIAG_DIR"); var container = Get("CONTAINER_NAME");
    var profiler = new BenchmarkProfiling(state);
    var failures = new List<string>();
    if (Get("DOTNET_TRACE", "false") == "true") await Attempt("dotnet-trace collection", async () =>
    {
        var host = Get("DOTNET_TRACE_PID"); var collector = Get("DOTNET_TRACE_COLLECTOR_PID");
        if (host.Length == 0 || collector.Length == 0) throw new IOException("dotnet-trace was requested but its collector was never attached (no PID persisted)");
        if (!await Alive(host)) throw new IOException("the dotnet-trace collector exited before it was stopped; the trace does not cover the measured phase");
        var runningHost = await profiler.FindHostPidAsync(container, true, cancellation.Token);
        if (runningHost is null || profiler.ContainerPid(runningHost) != collector) throw new IOException($"the dotnet-trace collector in {container} changed identity; refusing to signal");
        if ((await BenchmarkProcess.RunAsync("docker", ["exec", container, "sh", "-c", "kill -INT \"$1\"", "sh", collector], cancellation.Token)).Code != 0) throw new IOException("could not signal dotnet-trace collector");
        var limit = int.Parse(Get("DOTNET_TRACE_STOP_TIMEOUT", "120"), CultureInfo.InvariantCulture);
        for (var index = 0; index < limit && await Alive(host); index++) await Task.Delay(1000, cancellation.Token);
        if (await Alive(host)) throw new IOException($"dotnet-trace did not finish within {limit}s of SIGINT");
        var trace = Path.Combine(diag, "dotnet-trace", $"rpcbench{suffix}.nettrace");
        if (!Nonempty(trace)) throw new IOException($"dotnet-trace produced no {trace}");
    });
    if (Get("PERF", "false") == "true") await Attempt("perf profiling", async () =>
    {
        foreach (var key in new[] { "PERF_PID", "PERF_NODE_PID", "PERF_CONTAINER_PID", "PERF_RECORDER_START_TIME", "PERF_RECORDER_COMM", "PERF_RECORDER_EXE" })
            if (Get(key).Length == 0) throw new IOException("perf was requested but its recorder or client PID was not persisted");
        var pid = Get("PERF_PID"); var proc = Get("RPC_BENCH_PROC_ROOT", "/proc");
        var identity = (Get("PERF_RECORDER_START_TIME"), Get("PERF_RECORDER_COMM"), Get("PERF_RECORDER_EXE"));
        if (!await BenchmarkProfiling.SignalPerfAsync("INT", pid, identity.Item1, identity.Item2, identity.Item3, proc, cancellation.Token)) throw new IOException($"perf recorder changed identity or exited; refusing to signal pid {pid}");
        for (var index = 0; index < 120 && await Alive(pid); index++)
        {
            if (BenchmarkProfiling.Identity(pid, proc) != identity) throw new IOException("perf recorder changed identity while stopping; refusing further signals");
            await Task.Delay(1000, cancellation.Token);
        }
        if (await Alive(pid))
        {
            await BenchmarkProfiling.SignalPerfAsync("KILL", pid, identity.Item1, identity.Item2, identity.Item3, proc, cancellation.Token);
            throw new IOException("perf did not stop cleanly");
        }
        var data = Path.Combine(diag, "perf", $"perf{suffix}.data");
        if (!Nonempty(data)) throw new IOException("perf recorded no data");
        var sourceMap = $"/tmp/perf-{Get("PERF_CONTAINER_PID")}.map";
        var targetMap = $"/tmp/perf-{Get("PERF_NODE_PID")}.map";
        if ((await BenchmarkProcess.RunAsync("docker", ["exec", container, "test", "-s", sourceMap], cancellation.Token)).Code != 0) throw new IOException($"could not prepare the Nethermind perf map from {sourceMap}");
        if (sourceMap != targetMap && (await BenchmarkProcess.RunAsync("docker", ["exec", container, "cp", sourceMap, targetMap], cancellation.Token)).Code != 0) throw new IOException($"could not copy the Nethermind perf map from {sourceMap}");
        var folded = Path.Combine(diag, "perf", $"perf{suffix}.folded");
        var temporary = folded + ".tmp";
        var start = new ProcessStartInfo("perf") { RedirectStandardOutput = true };
        foreach (var argument in new[] { "script", "--symfs", $"/proc/{Get("PERF_NODE_PID")}/root", "--input", data }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start perf script");
        using var stop = cancellation.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException exception) { Console.Error.WriteLine(exception.Message); }
        });
        try
        {
            await using (var output = File.CreateText(temporary))
                foreach (var (stack, count) in Profiles.Fold(process.StandardOutput)) await output.WriteLineAsync($"{stack} {count}".AsMemory(), cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            if (process.ExitCode != 0) throw new IOException($"perf script exited {process.ExitCode}");
            if (!Profiles.Read(temporary).Any(row => row.Count > 0 && row.Stack.Contains(';') && Profiles.IsManaged(row.Stack[(row.Stack.LastIndexOf(';') + 1)..]))) throw new IOException("folded profile has no managed leaf samples; check perf-map symbolization");
            File.Move(temporary, folded, overwrite: true);
            BenchmarkProcess.Log($"perf profile folded: {File.ReadLines(folded).LongCount()} stacks");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException exception) { Console.Error.WriteLine(exception.Message); }
            await process.WaitForExitAsync(CancellationToken.None);
            File.Delete(temporary);
        }
    });
    var dumpName = $"rpcbench{suffix}.dmp";
    string? dumpRoot = null;
    if (Get("DOTNET_DUMP", "false") == "true") await Attempt("dotnet-dump collect", async () =>
    {
        dumpRoot = await profiler.DotnetRootAsync(container, cancellation.Token) ?? throw new IOException($"no .NET root with host/fxr found inside {container}");
        var result = await BenchmarkProcess.RunAsync("timeout", [Get("DOTNET_DUMP_TIMEOUT", "1200"), "docker", "exec", "-e", "DOTNET_ROOT=" + dumpRoot, "-e", "DOTNET_ROLL_FORWARD=Major", container, "/opt/dotnet-dump/dotnet-dump", "collect", "--name", "nethermind", "--type", "Heap", "--output", "/dotnet-dump-output/" + dumpName], cancellation.Token);
        Console.Write(result.Output); Console.Error.Write(result.Error);
        if (result.Code != 0) throw new IOException("dotnet-dump collect failed");
        await BenchmarkProcess.RunAsync("docker", ["exec", container, "chmod", "0644", "/dotnet-dump-output/" + dumpName], cancellation.Token);
    });
    BenchmarkProcess.Log($"Stopping container '{container}' (grace {Get("STOP_GRACE", "180")}s)...");
    await BenchmarkProcess.RunAsync("docker", ["stop", "-t", Get("STOP_GRACE", "180"), container], cancellation.Token);
    var log = Get("LOG_OUT", Path.Combine(directory, $"node{suffix}.log"));
    await BenchmarkFiles.RunCombinedToFileAsync("docker", ["logs", container], log, cancellation.Token);
    if (Get("PREPARED_SNAPSHOT_ROOT").Length > 0) await Attempt("prepared fixture shutdown and integrity", async () =>
    {
        var script = Path.Combine(BenchmarkProcess.EnvironmentValue("RPC_BENCH_SCRIPT_ROOT", Path.Combine(Environment.CurrentDirectory, "scripts", "rpc-bench")), "prepared-snapshot.py");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        bounded.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var checkedFixture = await BenchmarkProcess.RunAsync("python3", [script, "stopped", "--root", Get("PREPARED_SNAPSHOT_ROOT"), "--manifest-sha256", Get("PREPARED_SNAPSHOT_SHA256"), "--image", Get("NODE_IMAGE"), "--container", container, "--output", directory], bounded.Token);
            if (checkedFixture.Code != 0) throw new IOException(checkedFixture.Error);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new IOException("prepared fixture shutdown check timed out"); }
    });
    if (Get("DOTNET_DUMP", "false") == "true" && !failures.Contains("dotnet-dump collect"))
    {
        foreach (var (name, command) in new[] { ("gcheapstat", "gcheapstat"), ("eeheap-gc", "eeheap -gc"), ("dumpheap-stat", "dumpheap -stat"), ("dumpheap-stat-live", "dumpheap -stat -live"), ("dumpheap-stat-dead", "dumpheap -stat -dead"), ("dumpheap-stat-loh", "dumpheap -stat -min 85000"), ("sizestats", "sizestats") })
            await Attempt("dotnet-dump report " + name, async () =>
            {
                var code = await BenchmarkFiles.RunCombinedToFileAsync("timeout", [Get("DOTNET_DUMP_TIMEOUT", "1200"), "docker", "run", "--rm", "--network", "none", "-v", Get("DOTNET_DUMP_HOST_PATH") + ":/opt/dotnet-dump:ro", "-v", diag + "/dotnet-dump:/dotnet-dump-output:ro", "--entrypoint", "/usr/bin/env", Get("NODE_IMAGE"), "DOTNET_ROOT=" + dumpRoot, "DOTNET_ROLL_FORWARD=Major", "/opt/dotnet-dump/dotnet-dump", "analyze", "/dotnet-dump-output/" + dumpName, "--command", command, "--command", "exit"], Path.Combine(diag, "dotnet-dump", name + ".txt"), cancellation.Token);
                if (code != 0) throw new IOException($"dotnet-dump report '{name}' failed");
            });
        if (Get("DOTNET_DUMP_KEEP", "false") != "true") File.Delete(Path.Combine(diag, "dotnet-dump", dumpName));
    }
    if (Get("DOTTRACE") == "true" && Directory.Exists(Path.Combine(diag, "dottrace")))
    {
        BenchmarkProcess.Log($"dotTrace snapshots under {diag}/dottrace:");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(diag, "dottrace"), "*", SearchOption.AllDirectories)) Console.WriteLine("  " + path);
    }
    var final = Path.Combine(directory, $"db-final{suffix}.txt"); var baseline = Path.Combine(directory, $"db-baseline{suffix}.txt");
    await Attempt("DB integrity check", async () =>
    {
        await BenchmarkFiles.FingerprintAsync(Get("DB_SOURCE"), final, cancellation.Token);
        var equal = File.Exists(baseline) && File.ReadLines(baseline).SequenceEqual(File.ReadLines(final));
        if (!equal)
        {
            var difference = await BenchmarkProcess.RunAsync("diff", [baseline, final], cancellation.Token);
            Console.WriteLine(string.Join('\n', difference.Output.Split('\n').Take(Get("DB_ISOLATION") == "direct" ? 40 : 60)));
            if (Get("DB_ISOLATION") != "direct") throw new IOException("DB SNAPSHOT WAS MODIFIED during the run — this must never happen");
            BenchmarkProcess.Log("::warning::direct mode: snapshot changed as expected");
        }
        else if (Get("DB_ISOLATION") != "direct" && Get("SCRATCH_ROOT").Length > 0)
        {
            try
            {
                var anchors = Path.Combine(Get("SCRATCH_ROOT"), "fingerprints"); Directory.CreateDirectory(anchors);
                File.Copy(final, Path.Combine(anchors, Path.GetFileName(Get("DB_SOURCE")) + ".txt"), overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { BenchmarkProcess.Log("::warning::could not refresh fingerprint anchor: " + exception.Message); }
            BenchmarkProcess.Log($"  OK — snapshot unchanged ({File.ReadLines(final).LongCount()} fingerprint lines match).");
        }
    }, warnOnly: Get("DB_ISOLATION") == "direct");
    await BenchmarkProcess.RunAsync("docker", ["rm", "-fv", container], cancellation.Token);
    var run = Get("RUN_SCRATCH");
    BenchmarkPaths.AssertSane(run, "RUN_SCRATCH");
    var mountPath = Get("DB_ISOLATION") switch { "overlay" => Path.Combine(run, "merged"), "readonly-bind" => Path.Combine(run, "ro"), _ => "" };
    if (mountPath.Length > 0 && (await BenchmarkProcess.RootAsync("umount", [mountPath], cancellation.Token)).Code != 0)
    {
        if (Get("PREPARED_SNAPSHOT_ROOT").Length > 0) throw new IOException("prepared child remains mounted; refusing lazy unmount or deletion");
        await BenchmarkProcess.RootAsync("umount", ["-l", mountPath], cancellation.Token);
    }
    if (BenchmarkPaths.MountsUnder(await BenchmarkPaths.CanonicalAsync(run, false, cancellation.Token)).Length > 0) throw new IOException($"refusing to delete '{run}' — still mounted");
    await BenchmarkProcess.RootAsync("rm", ["-rf", "--", run], cancellation.Token);
    BenchmarkProcess.Log("  scratch removed.");
    if (failures.Count > 0) throw new IOException(string.Join(", ", failures) + " FAILED");
    BenchmarkProcess.Log("=== Node stopped; snapshot verified pristine ==="); return 0;

    async Task<bool> Alive(string pid) => (await BenchmarkProcess.RunAsync("kill", ["-0", pid], cancellation.Token)).Code == 0;
    static bool Nonempty(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
    async Task Attempt(string name, Func<Task> action, bool warnOnly = false)
    {
        try { await action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            BenchmarkProcess.Log($"{(warnOnly ? "::warning::" : "::error::")} {name}: {exception.Message}");
            if (name == "dotnet-trace collection")
            {
                var traceLog = Path.Combine(diag, "dotnet-trace", $"dotnet-trace-collect{suffix}.log");
                try
                {
                    if (File.Exists(traceLog))
                        foreach (var line in File.ReadLines(traceLog).TakeLast(20)) Console.Error.WriteLine("    " + line);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(error.Message); }
            }
            if (!warnOnly) failures.Add(name);
        }
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("Node teardown cancelled; persisted state retained."); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}"); return 1;
}
finally { Console.CancelKeyPress -= cancel; }
