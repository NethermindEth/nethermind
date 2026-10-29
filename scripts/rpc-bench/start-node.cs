#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include benchmark-process.helpers.cs
#:include benchmark-paths.helpers.cs
#:include benchmark-files.helpers.cs
#:include benchmark-state.helpers.cs
#:include benchmark-profiling.helpers.cs
#:include benchmark-rpc.helpers.cs
#:include node-configuration.helpers.cs

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    var state = new Dictionary<string, string>(StringComparer.Ordinal);
    string Get(string key, string fallback = "")
    {
        if (!state.TryGetValue(key, out var value)) state[key] = value = BenchmarkProcess.EnvironmentValue(key, fallback);
        return value;
    }
    foreach (var name in new[] { "DB_SOURCE", "SCRATCH_ROOT", "STATE_DIR" })
        if (Get(name).Length == 0) throw new ArgumentException($"{name} is required");
    var client = Get("CLIENT", "nethermind"); var instance = Get("INSTANCE", "primary");
    var image = Get("NODE_IMAGE", BenchmarkProcess.EnvironmentValue("NETHERMIND_IMAGE"));
    if (image.Length == 0) throw new ArgumentException("NODE_IMAGE (docker image reference to run) is required");
    var suffix = instance switch { "primary" => "", "reference" => "-reference", _ => throw new ArgumentException($"unknown INSTANCE '{instance}' (expected primary | reference)") };
    var nodeArguments = NodeConfiguration.Arguments(Get);
    var isolation = Get("DB_ISOLATION", "overlay");
    if (isolation is not ("overlay" or "copy" or "readonly-bind" or "direct")) throw new ArgumentException($"unknown DB_ISOLATION '{isolation}' (expected overlay | copy | readonly-bind | direct)");
    var container = Get("CONTAINER_NAME", "rpcbench-" + instance);
    var rpcPort = Get("RPC_PORT", "8545"); var network = Get("NETWORK", "mainnet");
    var dottrace = Get("DOTTRACE", "false"); var perf = Get("PERF", "false");
    var trace = Get("DOTNET_TRACE", "false"); var dump = Get("DOTNET_DUMP", "false");
    var afterWarmup = Get("PROFILE_AFTER_WARMUP", "false");
    foreach (var name in new[] { "DOTNET_TRACE", "DOTNET_DUMP", "PROFILE_AFTER_WARMUP" })
        if (Get(name) is not ("true" or "false")) throw new ArgumentException($"{name} must be true or false (got '{Get(name)}')");
    foreach (var name in new[] { "DOTTRACE", "PERF", "DOTNET_TRACE", "DOTNET_DUMP" })
        if (Get(name) == "true" && client != "nethermind") throw new ArgumentException($"{name.ToLowerInvariant().Replace('_', '-')} requires CLIENT=nethermind");
    var mode = Get("DOTTRACE_MODE", "sampling");
    if (mode is not ("sampling" or "tracing" or "timeline")) throw new ArgumentException($"DOTTRACE_MODE must be sampling, tracing, or timeline (got '{mode}')");
    state["DOTTRACE_DEFERRED"] = dottrace == "true" && afterWarmup == "true" ? "true" : "false";
    Get("PERF_FREQUENCY", "99");
    var profiler = new BenchmarkProfiling(state);
    if (perf == "true") await profiler.RequirePerfAsync(cancellation.Token);
    Directory.CreateDirectory(Get("STATE_DIR"));
    if (!Directory.Exists(Get("DB_SOURCE")))
    {
        BenchmarkProcess.Log($"DB_SOURCE '{Get("DB_SOURCE")}' is not a directory. Snapshot candidates on this runner:");
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        IEnumerable<string> Directories(string path) => Directory.EnumerateDirectories(path, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 });
        foreach (var root in new[] { "/mnt", "/data" })
            if (Directory.Exists(root))
                foreach (var child in Directories(root))
                    foreach (var candidate in Directories(child).SelectMany(path => new[] { path }.Concat(Directories(path))))
                        if (SnapshotNameRegex().IsMatch(Path.GetFileName(candidate)) || root == "/data" && Path.GetFileName(candidate).Contains('-')) candidates.Add(candidate);
        Console.WriteLine(candidates.Count > 0 ? string.Join('\n', candidates.Select(path => "  " + path)) : "  <none found under /mnt or /data>");
        throw new IOException("set node_config.db_source to a valid snapshot path");
    }
    var (database, scratch) = await BenchmarkPaths.GuardAsync(Get("DB_SOURCE"), Get("SCRATCH_ROOT"), cancellation.Token);
    state["DB_SOURCE"] = database; state["SCRATCH_ROOT"] = scratch;
    var armScratch = Get("ARM_SCRATCH_DIR");
    if (armScratch.Length > 0)
    {
        armScratch = await BenchmarkPaths.CanonicalAsync(armScratch, true, cancellation.Token);
        BenchmarkPaths.AssertSane(armScratch, "ARM_SCRATCH_DIR");
        if (!Directory.Exists(armScratch)) throw new IOException($"ARM_SCRATCH_DIR '{armScratch}' is not a directory");
        if (armScratch == scratch || !BenchmarkPaths.Contains(scratch, armScratch)) throw new IOException("ARM_SCRATCH_DIR must be a child of SCRATCH_ROOT");
        state["ARM_SCRATCH_DIR"] = armScratch;
    }
    var diag = Get("DIAG_DIR", Path.Combine(scratch, "diag"));
    BenchmarkProcess.Log($"=== RPC benchmark node startup ===\nClient: {client} (instance: {instance})\nImage: {image}\nSnapshot: {database} ({(isolation == "direct" ? "READ-WRITE bind — direct mode" : "read-only")})\nIsolation: {isolation}\nScratch: {scratch}\ndotTrace: {dottrace}\nperf: {perf} ({Get("PERF_FREQUENCY")}Hz)\ndotnet-trace: {trace}\ndotnet-dump: {dump}\nRPC port: {rpcPort} (network: {network})");
    foreach (var name in new[] { "_snapshot_metadata.json", "_snapshot_web3_clientVersion.json" })
        if (File.Exists(Path.Combine(database, name)))
        {
            var text = File.ReadAllText(Path.Combine(database, name)).Replace("\n", "", StringComparison.Ordinal);
            BenchmarkProcess.Log($"  {name}: {text[..Math.Min(300, text.Length)]}");
        }
    var baseline = Path.Combine(Get("STATE_DIR"), $"db-baseline{suffix}.txt");
    BenchmarkProcess.Log("Computing DB integrity baseline (tamper tripwire)...");
    await BenchmarkFiles.FingerprintAsync(database, baseline, cancellation.Token);
    await using (var stream = File.OpenRead(baseline)) BenchmarkProcess.Log($"  baseline: {File.ReadLines(baseline).LongCount()} lines, sha256={Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation.Token))}");
    var anchor = Path.Combine(scratch, "fingerprints", Path.GetFileName(database) + ".txt");
    Directory.CreateDirectory(Path.GetDirectoryName(anchor)!);
    if (isolation == "direct" && File.Exists(anchor)) Console.WriteLine("::warning::direct mode does not refresh the fingerprint anchor; any existing anchor is diagnostic only.");
    if (File.Exists(anchor) && File.ReadLines(anchor).FirstOrDefault() == File.ReadLines(baseline).FirstOrDefault() && !File.ReadLines(anchor).SequenceEqual(File.ReadLines(baseline)))
        BenchmarkProcess.Log($"::warning::Snapshot fingerprint differs from the last verified run's anchor ({anchor}) — an interrupted run may have modified it.");
    if (Get("PREPARED_SNAPSHOT_ROOT").Length == 0 && instance == "primary") await BenchmarkPaths.ReapAsync(["rpcbench-", "nethermind-rpcbench", "ethcallchaos-bench", "jsonbench-"], cancellation.Token);
    var run = Path.Combine(scratch, "run" + suffix);
    foreach (var mount in new[] { Path.Combine(run, "merged"), Path.Combine(run, "ro") })
        if ((await BenchmarkProcess.RunAsync("mountpoint", ["-q", mount], cancellation.Token)).Code == 0)
            if ((await BenchmarkProcess.RootAsync("umount", [mount], cancellation.Token)).Code != 0)
            {
                if (Get("PREPARED_SNAPSHOT_ROOT").Length > 0) throw new IOException("prepared child remains mounted; refusing lazy unmount or deletion");
                await BenchmarkProcess.RootAsync("umount", ["-l", mount], cancellation.Token);
            }
    await Reset(run); Directory.CreateDirectory(diag);
    var source = database; var mountOption = "rw";
    switch (isolation)
    {
        case "overlay":
            foreach (var name in new[] { "upper", "work", "merged" }) Directory.CreateDirectory(Path.Combine(run, name));
            source = Path.Combine(run, "merged");
            var options = $"lowerdir={database},upperdir={run}/upper,workdir={run}/work";
            if (Get("PREPARED_SNAPSHOT_ROOT").Length > 0)
                await Required("mount", ["-t", "overlay", "overlay", "-o", options + ",redirect_dir=off,metacopy=off,index=off", source], true);
            else if ((await BenchmarkProcess.RootAsync("mount", ["-t", "overlay", "overlay", "-o", options + ",redirect_dir=on,metacopy=on,volatile", source], cancellation.Token)).Code != 0)
                await Required("mount", ["-t", "overlay", "overlay", "-o", options, source], true);
            break;
        case "copy":
            source = Path.Combine(run, "db"); Directory.CreateDirectory(source);
            await Required("cp", ["-a", "--reflink=auto", database + "/.", source + "/"]);
            break;
        case "readonly-bind":
            source = Path.Combine(run, "ro"); Directory.CreateDirectory(source);
            await Required("mount", ["--bind", database, source], true);
            await Required("mount", ["-o", "remount,ro,bind", source], true); mountOption = "ro";
            break;
        case "direct": BenchmarkProcess.Log("::warning::db_isolation=direct — the pristine snapshot is mounted READ-WRITE and the node's startup writes will modify it."); break;
    }
    var target = Get("DATA_DIR_TARGET", "/execution-data") + (client == "geth" ? "/geth" : "");
    state["INSTANCE_SUFFIX"] = suffix; state["RUN_SCRATCH"] = run;
    Get("DOTNET_DUMP_HOST_PATH", "/opt/dotnet-dump");
    var statePath = Path.Combine(Get("STATE_DIR"), $"node{suffix}.env");
    File.WriteAllText(statePath, "");
    foreach (var key in new[] { "CLIENT", "INSTANCE", "INSTANCE_SUFFIX", "CONTAINER_NAME", "DB_ISOLATION", "RUN_SCRATCH", "SCRATCH_ROOT", "ARM_SCRATCH_DIR", "DB_SOURCE", "DIAG_DIR", "DOTTRACE", "DOTTRACE_DEFERRED", "PERF", "PERF_FREQUENCY", "DOTNET_TRACE", "DOTNET_DUMP", "DOTNET_DUMP_HOST_PATH", "NODE_IMAGE", "PROFILE_AFTER_WARMUP", "RPC_PORT", "PREPARED_SNAPSHOT_ROOT", "PREPARED_SNAPSHOT_SHA256" }) BenchmarkState.Append(statePath, key, Get(key));
    var docker = new List<string> { "run", "-d", "--name", container, "--restart", "no", "--stop-signal", "SIGINT", "-p", $"127.0.0.1:{rpcPort}:8545", "-v", $"{source}:{target}:{mountOption}" };
    if (armScratch.Length > 0) docker.AddRange(["--mount", $"type=bind,source={armScratch},target={armScratch}"]);
    foreach (var value in NodeConfiguration.SplitFlags(Get("NODE_ENV_VARS"))) docker.AddRange(["-e", value]);
    var perfEnvironment = new[] { "DOTNET_PerfMapEnabled=1", "DOTNET_PerfMapShowOptimizationTiers=1", "DOTNET_EnableWriteXorExecute=0" };
    if (perf == "true")
    {
        await Reset(Path.Combine(diag, "perf"));
        if (dottrace != "true") foreach (var value in perfEnvironment) docker.AddRange(["-e", value]);
    }
    foreach (var (enabled, tool, version) in new[] { (trace, "dotnet-trace", Get("DOTNET_TRACE_VERSION", "9.0.661903")), (dump, "dotnet-dump", Get("DOTNET_DUMP_VERSION", "10.0.745401")) })
        if (enabled == "true")
        {
            var host = Get(tool.ToUpperInvariant().Replace('-', '_') + "_HOST_PATH", "/opt/" + tool);
            await Install(tool, tool, host, version);
            await Reset(Path.Combine(diag, tool));
            docker.AddRange(["-v", $"{host}:/opt/{tool}:ro", "-v", $"{diag}/{tool}:/{tool}-output:rw"]);
        }
    foreach (var (key, flag) in new[] { ("NODE_CPUSET", "--cpuset-cpus"), ("NODE_MEMORY", "--memory") }) if (Get(key).Length > 0) docker.AddRange([flag, Get(key)]);
    var entry = new List<string>();
    if (dottrace == "true")
    {
        var host = Get("DOTTRACE_HOST_PATH", "/opt/dottrace"); await Install("JetBrains.dotTrace.GlobalTools", "dottrace", host, "");
        await Reset(Path.Combine(diag, "dottrace"));
        docker.AddRange(["-v", $"{host}:/opt/dottrace:ro", "-v", $"{diag}/dottrace:/dottrace-output:rw", "--entrypoint", "/opt/dottrace/dottrace"]);
        entry.AddRange(["start", "--framework=NetCore", "--profiling-type=" + char.ToUpperInvariant(mode[0]) + mode[1..], $"--save-to=/dottrace-output/rpcbench-{network}{suffix}.{(mode == "timeline" ? "dtt" : "dtp")}", "--propagate-exit-code"]);
        if (state["DOTTRACE_DEFERRED"] == "true")
        {
            var control = Path.Combine(diag, "dottrace", "control.svc"); File.WriteAllText(control, ""); await Required("chmod", ["a+rw", control]);
            entry.AddRange(["--collect-data-from-start=off", "--service-output=on", "--service-input=/dottrace-output/control.svc"]);
        }
        entry.Add("--");
        if (perf == "true") { entry.Add("/usr/bin/env"); entry.AddRange(perfEnvironment); }
        entry.Add("/nethermind/nethermind");
    }
    await BenchmarkProcess.RunAsync("docker", ["rm", "-fv", container], cancellation.Token);
    BenchmarkProcess.Log($"Starting {client} container '{container}'...\n  node args: {string.Join(' ', nodeArguments)}");
    docker.Add(image); docker.AddRange(entry); docker.AddRange(nodeArguments);
    await Required("docker", docker);
    await BenchmarkRpc.WaitAsync($"http://localhost:{rpcPort}", int.Parse(Get("HEALTH_TIMEOUT", "1800"), CultureInfo.InvariantCulture), container, cancellation.Token);
    BenchmarkProcess.Log("=== Node ready for benchmarking ===");
    if (afterWarmup == "true") BenchmarkProcess.Log("profilers deferred: run start-profilers.cs after the warm-up");
    else if (perf == "true" || trace == "true") await profiler.StartAsync(statePath, cancellation.Token);
    return 0;

    async Task Required(string executable, IEnumerable<string> arguments, bool root = false)
    {
        var result = root ? await BenchmarkProcess.RootAsync(executable, arguments, cancellation.Token) : await BenchmarkProcess.RunAsync(executable, arguments, cancellation.Token);
        Console.Write(result.Output); Console.Error.Write(result.Error);
        if (result.Code != 0) throw new IOException($"{executable} failed with exit code {result.Code}");
    }
    async Task Reset(string path)
    {
        var canonical = await BenchmarkPaths.CanonicalAsync(path, false, cancellation.Token);
        if (BenchmarkPaths.MountsUnder(canonical).Length > 0) throw new IOException($"refusing to delete '{path}' — still mounted");
        await Required("rm", ["-rf", "--", path], true); Directory.CreateDirectory(path);
    }
    async Task Install(string package, string executable, string path, string version)
    {
        if ((await BenchmarkProcess.RunAsync("test", ["-x", Path.Combine(path, executable)], cancellation.Token)).Code == 0) return;
        var arguments = new List<string> { "tool", "install", "--tool-path", path, package };
        if (version.Length > 0) arguments.AddRange(["--version", version]);
        if ((await BenchmarkProcess.RunAsync("dotnet", arguments, cancellation.Token)).Code != 0) await Required("dotnet", arguments, true);
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("Node startup cancelled; persisted state is available for teardown."); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}"); return 1;
}
finally { Console.CancelKeyPress -= cancel; }

partial class Program
{
    [GeneratedRegex("^[Nn]ethermind.*snapshot|^(nethermind|geth|reth)-")]
    private static partial Regex SnapshotNameRegex();
}
