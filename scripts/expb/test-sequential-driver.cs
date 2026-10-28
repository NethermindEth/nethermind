#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property DefineConstants=SCRIPT_TEST
#:include sequential-driver.cs

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

if (args.SequenceEqual(["orphan"]))
{
    using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
    using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("EXPB_TEST_PIPE")!, PipeDirection.Out, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(CancellationToken.None);
    using var writer = new StreamWriter(pipe) { AutoFlush = true };
    await writer.WriteLineAsync(Environment.ProcessId.ToString());
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if (args.Length > 0 && args[0] == "execute-scenarios")
{
    if (args.Contains("--help"))
    {
        Console.Error.WriteLine("No such option: --startup-block");
        return 2;
    }
    var startup = Environment.GetEnvironmentVariable("EXPB_TEST_STARTUP");
    var startupIndex = System.Array.IndexOf(args, "--startup-block");
    if (startup is not null && (startupIndex < 0 || startupIndex + 1 >= args.Length || args[startupIndex + 1] != startup)) return 11;
    var mode = Environment.GetEnvironmentVariable("EXPB_TEST_MODE");
    var configPath = args[System.Array.IndexOf(args, "--config-file") + 1];
    var config = JsonNode.Parse(File.ReadAllText(configPath))!;
    if (config["export"]?["secret"]?.ToString() != "private") return 9;
    if (Environment.GetEnvironmentVariable("EXPB_TEST_VALUE") != "value with spaces") return 10;
    if (mode == "orphan")
    {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "orphan") { UseShellExecute = false })!;
        await child.WaitForExitAsync();
        return 0;
    }
    if (mode is "cancel" or "kill")
    {
        using var exit = new CancellationTokenSource();
        using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; if (mode == "cancel") exit.Cancel(); });
        using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("EXPB_TEST_PIPE")!, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(CancellationToken.None);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(Environment.ProcessId.ToString());
        try { await Task.Delay(Timeout.Infinite, exit.Token); }
        catch (OperationCanceledException) when (exit.IsCancellationRequested) { }
        Console.WriteLine("Nethermind is shut down\nCleanup completed");
        return 0;
    }
    Console.WriteLine("[payload-server] client_metric block_number=100 processing_ms=20");
    Console.WriteLine("| 1 | 30000000 | 25.0 |");
    Console.WriteLine("Nethermind is shut down\nCleanup completed");
    Console.Error.WriteLine("retained stderr");
    return 0;
}

var tests = 0;
var temporary = Directory.CreateTempSubdirectory("sequential driver tests ");
try
{
    foreach (var (source, client, layout, fixture, expected) in new (string, string, string, string, string?)[]
    {
        ("/mnt/sda/nethermind-flat-25490000", "nethermind", "flat", "/mnt/sda/nethermind-flat-25490000", "25490000"),
        ("/data/nethermind/nethermind-flat-26000000", "nethermind", "flat", "/data/nethermind/nethermind-flat-26000000", "26000000"),
        ("/data/nethermind/nethermind-flat-0", "nethermind", "flat", "/data/nethermind/nethermind-flat-0", "0"),
        ("/snapshots/custom", "nethermind", "flat", "/mnt/sda/nethermind-flat-25490000", ""),
        ("/mnt/sda/nethermind-flat-25490000", "custom", "flat", "/mnt/sda/nethermind-flat-25490000", ""),
        ("/mnt/sda/nethermind-flat-25490000", "nethermind", "halfpath", "/mnt/sda/nethermind-flat-25490000", ""),
        ("", "nethermind", "flat", "", ""),
        ("/mnt/sda/nethermind-flat-invalid", "nethermind", "flat", "/mnt/sda/nethermind-flat-invalid", null),
        ("/mnt/sda/nethermind-flat--1", "nethermind", "flat", "/mnt/sda/nethermind-flat--1", null),
        ("/mnt/sda/nethermind-flat-01", "nethermind", "flat", "/mnt/sda/nethermind-flat-01", null)
    }) Check($"checkpoint scope {client}/{layout}/{source}", () =>
    {
        var driver = Driver(("CLIENT", client), ("STATE_LAYOUT", layout), ("FLAT_SNAPSHOT_BLOCK_DIR", fixture));
        var scenario = new JsonObject() { ["snapshot_source"] = source };
        if (expected is null) Reject(() => driver.StartupFlags(scenario));
        else Equal(string.Join('|', driver.StartupFlags(scenario)), expected.Length == 0 ? "" : "--startup-block|" + expected);
    });
    foreach (var (outcome, known) in new[] { ("", true), ("unsupported", true), ("timeout", true), ("missing", true), ("", false) })
        await CheckAsync($"checkpoint campaign preflight outcome={outcome} known={known}", async () =>
        {
            var root = Path.Combine(temporary.FullName, "checkpoint-" + outcome + known);
            var driver = Driver(("EXPB_CAMPAIGN_DIR", root), ("RUN_COUNT", "2"), ("EXPB_IMAGES_JSON", Images().ToJsonString()),
                ("STATE_LAYOUT", "flat"), ("FLAT_SNAPSHOT_BLOCK_DIR", "/data/nethermind/nethermind-flat-26000000"));
            var config = Base();
            config["scenarios"]!["nethermind"]!["snapshot_source"] = known ? "/mnt/sda/nethermind-flat-25490000" : "/snapshots/custom";
            var probes = 0;
            var samples = 0;
            Task<string> Capture(string command, string[] arguments, CancellationToken token)
            {
                probes++;
                Equal(command, "expb");
                Equal(string.Join('|', arguments), "execute-scenarios|--startup-block|26000000|--help");
                Equal(token.CanBeCanceled, true);
                return outcome switch
                {
                    "unsupported" => throw new IOException("No such option: --startup-block"),
                    "timeout" => throw new OperationCanceledException(token),
                    "missing" => throw new Win32Exception("expb was not found"),
                    _ => Task.FromResult("Usage: expb execute-scenarios")
                };
            }
            var result = await driver.Run(CancellationToken.None, _ => Task.FromResult(config), (_, _) => Task.CompletedTask,
                (_, currentImage, run, _, _) =>
                {
                    Equal(probes, known ? 1 : 0);
                    samples++;
                    return Task.FromResult(Sample(currentImage["id"]!.ToString(), run, "success", 25));
                }, probe: (flags, token) => driver.ProbeStartup(flags, token, Capture));
            Equal(result, outcome.Length == 0 ? 0 : 1);
            Equal(probes, known ? 1 : 0);
            Equal(samples, outcome.Length == 0 ? 6 : 0);
            if (outcome.Length > 0)
            {
                var campaign = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "campaign.json")))!;
                Equal(campaign["status"]!.ToString(), "failed");
                var error = campaign["failure_reasons"]![0]!.ToString();
                var expected = outcome switch { "unsupported" => "No such option: --startup-block", "missing" => "expb was not found", _ => "timed out after 30 seconds" };
                if (!error.Contains("snapshot checkpoint", StringComparison.Ordinal) || !error.Contains(expected, StringComparison.Ordinal)) throw new Exception("checkpoint failure lost context or cause: " + error);
            }
        });
    await CheckAsync("default campaign preflight rejects an unsupported checkpoint before samples", async () =>
    {
        var root = Path.Combine(temporary.FullName, "default checkpoint preflight");
        var driver = Driver(("EXPB_CAMPAIGN_DIR", root), ("EXPB_BIN", Environment.ProcessPath!), ("RUN_COUNT", "1"),
            ("EXPB_IMAGES_JSON", Images().ToJsonString()), ("STATE_LAYOUT", "flat"),
            ("FLAT_SNAPSHOT_BLOCK_DIR", "/data/nethermind/nethermind-flat-26000000"));
        var config = Base();
        config["scenarios"]!["nethermind"]!["snapshot_source"] = "/mnt/sda/nethermind-flat-25490000";
        var samples = 0;
        var result = await driver.Run(CancellationToken.None, _ => Task.FromResult(config), (_, _) => Task.CompletedTask,
            (_, currentImage, run, _, _) =>
            {
                samples++;
                return Task.FromResult(Sample(currentImage["id"]!.ToString(), run, "success", 25));
            });
        Equal(result, 1);
        Equal(samples, 0);
        var failure = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "campaign.json")))!["failure_reasons"]![0]!.ToString();
        if (!failure.Contains("snapshot checkpoint", StringComparison.Ordinal) || !failure.Contains("No such option: --startup-block", StringComparison.Ordinal)) throw new Exception("default checkpoint preflight did not retain the failure: " + failure);
    });
    await CheckAsync("checkpoint probe configures and enforces the thirty second deadline", async () =>
    {
        var clock = new ProbeTimeProvider();
        var timedOut = false;
        try
        {
            await Driver().ProbeStartup(["--startup-block", "26000000"], CancellationToken.None, (_, _, token) =>
            {
                Equal(clock.DueTime, TimeSpan.FromSeconds(30));
                Equal(clock.Period, Timeout.InfiniteTimeSpan);
                Equal(token.IsCancellationRequested, false);
                clock.Expire();
                Equal(token.IsCancellationRequested, true);
                return Task.FromCanceled<string>(token);
            }, clock);
        }
        catch (TimeoutException exception)
        {
            timedOut = true;
            if (!exception.Message.Contains("timed out after 30 seconds", StringComparison.Ordinal)) throw;
        }
        Equal(timedOut, true);
        Equal(clock.TimerDisposed, true);
    });
    await CheckAsync("checkpoint probe preserves caller cancellation", async () =>
    {
        var clock = new ProbeTimeProvider();
        using var caller = new CancellationTokenSource();
        var cancelled = false;
        try
        {
            await Driver().ProbeStartup(["--startup-block", "26000000"], caller.Token, (_, _, token) =>
            {
                caller.Cancel();
                Equal(token.IsCancellationRequested, true);
                return Task.FromCanceled<string>(token);
            }, clock);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { cancelled = true; }
        Equal(cancelled, true);
        Equal(clock.TimerDisposed, true);
    });
    foreach (var (input, expected) in new[]
    {
        ("", ""), ("--JsonRpc.GasCap=1000", "--JsonRpc.GasCap=1000"),
        ("--JsonRpc.GasCap 1000", "--JsonRpc.GasCap=1000"), ("--JsonRpc.GasCap,1000", "--JsonRpc.GasCap=1000"),
        ("--A=1\n--B 2\n--C\n3", "--A=1|--B=2|--C=3")
    }) Check($"parse flags {input}", () => Equal(string.Join('|', SequentialDriver.ParseFlags(input)), expected));
    foreach (var (input, expected) in new[] { ("", ""), ("A=1, B=2", "A=1|B=2"), ("A=", "A=") })
        Check($"parse pairs {input}", () => Equal(string.Join('|', SequentialDriver.ParsePairs(input).Select(pair => $"{pair.Key}={pair.Value}")), expected));
    foreach (var input in new[] { "A", "1A=2", "A B=2", "=2" }) Check($"reject pair {input}", () => Reject(() => SequentialDriver.ParsePairs(input)));
    Check("optional amount", () => { Equal(SequentialDriver.ParseAmount(""), null); Equal(SequentialDriver.ParseAmount("1000"), 1000L); });
    foreach (var input in new[] { "0", "-1", "1.5", "1000 ", "ten" }) Check($"reject amount {input}", () => Reject(() => SequentialDriver.ParseAmount(input)));
    Check("statistics median percentiles empty and unsorted", () =>
    {
        var stats = SequentialDriver.MetricStats(Enumerable.Range(1, 10).Select(value => (double)value));
        foreach (var (key, value) in new[] { ("count", 10d), ("avg", 5.5), ("median", 5.5), ("p90", 9d), ("p95", 10d), ("p99", 10d), ("min", 1d), ("max", 10d) }) Near(stats[key], value);
        Near(SequentialDriver.MetricStats([1, 2, 3])["median"], 2);
        foreach (var key in new[] { "median", "p90", "p99" }) Near(SequentialDriver.MetricStats([7])[key], 7);
        Near(SequentialDriver.MetricStats([3, 1, 2])["min"], 1);
        var empty = SequentialDriver.MetricStats([]);
        Near(empty["count"], 0);
        foreach (var key in new[] { "avg", "median", "p90", "p95", "p99", "min", "max" }) Equal(empty[key], null);
    });
    var fullLog = "\u001b[32m[payload-server] client_metric block_number=100 processing_ms=20\u001b[0m\n[payload-server] client_metric block_number=101 processing_ms=30\n| 1 | 30000000 | 25.0 |\n\u001b[1m| 2 | 30000000 | 35.0 |\u001b[0m\nNethermind is shut down\nevent=\"Cleanup completed\"\n";
    Check("SSE and K6 pairing strips ANSI", () =>
    {
        var (metrics, diagnostics) = Metrics(fullLog);
        Equal(metrics["source"]!.ToString(), "SSE");
        Near(metrics["delivered"], 2); Near(metrics["sse_count"], 2);
        Equal(metrics["ids"]!.ToJsonString(), "[100,101]"); Equal(metrics["payload_indices"]!.ToJsonString(), "[1,2]");
        Near(metrics["processing"]!["avg"], 25); Near(metrics["request"]!["avg"], 30); Near(metrics["outside"]!["avg"], 5);
        Near(metrics["mgas_s"], 1200); Near(metrics["request_mgas_s"], 1000);
        Equal(metrics["shutdown"]!.GetValue<bool>() && metrics["cleanup"]!.GetValue<bool>(), true);
        Equal(diagnostics.Values.Sum(lines => lines.Count), 0);
    });
    foreach (var records in new[] { 998, 999, 1000 }) Check($"feed pairing with {records}/1000 records", () =>
    {
        var log = new StringBuilder();
        for (var index = 0; index < records; index++) log.AppendLine($"[payload-server] client_metric block_number={100 + index} processing_ms=20");
        for (var index = 1; index <= 1000; index++) log.AppendLine($"| {index} | 30000000 | 25.0 |");
        var (metrics, _) = Metrics(log.ToString());
        Near(metrics["delivered"], 1000); Near(metrics["sse_count"], records); Near(metrics["processing"]!["avg"], 20);
        if (records == 998) { Equal(metrics["outside"]!["avg"], null); Equal(metrics["mgas_s"], null); }
        else { Near(metrics["outside"]!["avg"], 5); Near(metrics["mgas_s"], 1500); }
        Near(metrics["request_mgas_s"], 1200);
    });
    foreach (var useSse in new[] { true, false }) Check($"request fallback useSse={useSse}", () =>
    {
        var (metrics, _) = Metrics((useSse ? "" : "[payload-server] client_metric block_number=100 processing_ms=20\n") + "| 1 | 30000000 | 25.0 |\n", useSse);
        Equal(metrics["source"]!.ToString(), "TTFB"); Near(metrics["avg"], 25); Near(metrics["sse_count"], 0);
        Equal(metrics["outside"]!["avg"], null); Equal(metrics["mgas_s"], null); Near(metrics["request_mgas_s"], 1200);
    });
    Check("empty metrics have no source", () => { var (metrics, _) = Metrics(""); Equal(metrics["source"]!.ToString(), "none"); Equal(metrics["avg"], null); Near(metrics["count"], 0); });
    Check("diagnostics count all and retain bounded lines", () =>
    {
        var (metrics, diagnostics) = Metrics(string.Concat(Enumerable.Range(0, 50).Select(index => $"System.Exception: {new string('x', 9000)} {index}\n")) + "Invalid Blocks detected\nFATAL: node died\n");
        Near(metrics["exception_count"], 50); Near(metrics["invalid_count"], 1); Near(metrics["severe_count"], 1);
        Equal(diagnostics["exceptions"].Count, SequentialDriver.MaxDiagnosticLines);
        if (diagnostics["exceptions"].Any(line => line.Length > SequentialDriver.Limit + 80)) throw new Exception("Unbounded diagnostic");
    });

    var image = Object("""{"id":"image-1-abc","image":"nethermindeth/nethermind:tag"}""");
    Check("compute warm gas cap", () =>
    {
        var (config, name) = Driver(("MEASUREMENT_MODE", "compute-warm")).Render(Base(), image, 1);
        Equal(name, "nethermind-image-1-abc-run1");
        Equal(config["scenarios"]![name]!["extra_flags"]!.ToJsonString(), "[\"--JsonRpc.GasCap=1000000000000\"]");
        Equal(config["scenarios"]![name]!["image"]!.ToString(), image["image"]!.ToString());
    });
    foreach (var pair in new[] { ("ADDITIONAL_EXTRA_FLAGS", "--JsonRpc.GasCap=100"), ("EXPB_ENV_PASSTHROUGH", "EXPB_EVM_WARMUP=1") })
        Check($"compute warm rejects {pair.Item1}", () => Reject(() => Driver(("MEASUREMENT_MODE", "compute-warm"), pair).Render(Base(), image, 1)));
    Check("standard flags env tracing and substitutions", () =>
    {
        var (config, name) = Driver(("ADDITIONAL_EXTRA_FLAGS", "--JsonRpc.GasCap=100"), ("CLIENT_ENV", "A=1"), ("TRACE_BLOCKS", "123"), ("AMOUNT", "42"), ("EXPB_DATA_DIR", "data with spaces")).Render(Object("""{"scenarios":{"nethermind":{"path":"/mnt/sda/expb-data/<<AMOUNT>>"}}}"""), image, 2);
        var scenario = config["scenarios"]![name]!;
        Equal(scenario["extra_flags"]!.ToJsonString(), "[\"--JsonRpc.GasCap=100\"]");
        Equal(scenario["extra_env"]!["A"]!.ToString(), "1"); Equal(scenario["extra_env"]!["NETHERMIND_PROFILE_BLOCKS"]!.ToString(), "123");
        Equal(scenario["path"]!.ToString(), "data with spaces/42"); Near(scenario["amount"], 42);
    });
    Check("reference client resets Nethermind settings", () =>
    {
        var original = Object("""{"scenarios":{"nethermind":{"extra_flags":["--FlatDb.Enabled=true"],"extra_env":{"X":"1"},"extra_volumes":{"a":"b"}}}}""");
        var (config, name) = Driver(("CLIENT", "reth"), ("CLIENT_SNAPSHOT_DIR", "/snapshot"), ("SNAPSHOT_MOUNT_PATH", "/execution-data"), ("ADDITIONAL_EXTRA_FLAGS", "--engine.slow-block-threshold=0")).Render(original, image, 1);
        var scenario = config["scenarios"]![name]!;
        Equal(name, "reth-image-1-abc-run1"); Equal(scenario["client"]!.ToString(), "reth"); Equal(scenario["snapshot_source"]!.ToString(), "/snapshot");
        Equal(scenario["snapshot_backend"]!.ToString(), "overlay"); Equal(scenario["snapshot_mount_path"]!.ToString(), "/execution-data"); Near(scenario["startup_wait"], 600);
        Equal(scenario["extra_flags"]!.ToJsonString(), "[\"--engine.slow-block-threshold=0\"]");
        Equal(scenario["extra_env"]!.ToJsonString(), "{}"); Equal(scenario["extra_volumes"]!.ToJsonString(), "{}"); Equal(scenario["extra_commands"]!.ToJsonString(), "[]");
    });
    foreach (var pair in new[] { ("MEASUREMENT_MODE", "compute-warm"), ("TRACE_BLOCKS", "25490001"), ("CLIENT_ENV", "DOTNET_gcServer=1"), ("CLIENT_SNAPSHOT_DIR", "") })
        Check($"reference client rejects {pair.Item1}", () => Reject(() => Driver(("CLIENT", "geth"), ("CLIENT_SNAPSHOT_DIR", "/snapshot"), ("SNAPSHOT_MOUNT_PATH", "/data"), pair).Render(Base(), image, 1)));
    Check("reject unknown client", () => Reject(() => Driver(("CLIENT", "erigon")).Render(Base(), image, 1)));
    Check("reject missing scenario", () => Reject(() => Driver().Render(Object("{\"scenarios\":{}}"), image, 1)));
    var topology = Path.Combine(temporary.FullName, "topology");
    for (var cpu = 0; cpu < 16; cpu++)
    {
        var path = Directory.CreateDirectory(Path.Combine(topology, $"cpu{cpu}", "topology"));
        File.WriteAllText(Path.Combine(path.FullName, "thread_siblings_list"), $"{cpu % 8},{cpu % 8 + 8}\n");
    }
    var withResources = Object("""{"resources":{"cpu":8,"cpuset":"2-7,10-15","infra_cpuset":"0-1,8-9","mem":"64g"},"scenarios":{"nethermind":{}}}""");
    Check("whole core pinning preserves original config", () =>
    {
        var (config, _) = Driver(("CPU_TOPOLOGY_DIR", topology)).Render(withResources, image, 1);
        Equal(config["resources"]!["cpuset"]!.ToString(), "2,3,4,5,10,11,12,13"); Near(config["resources"]!["cpu"], 0); Near(withResources["resources"]!["cpu"], 8);
        var odd = withResources.DeepClone().AsObject(); odd["resources"]!["cpu"] = 7;
        Reject(() => Driver(("CPU_TOPOLOGY_DIR", topology)).Render(odd, image, 1));
    });
    foreach (var (label, values, expected) in new[]
    {
        ("wider cpuset", new[] { ("DOCKER_CPU", "12"), ("DOCKER_CPUSET", "0-15"), ("DOCKER_INFRA_CPUSET", "0-1") }, "0,1,2,3,4,5,8,9,10,11,12,13"),
        ("all limits", new[] { ("DOCKER_CPU", "all"), ("DOCKER_CPUSET", "all"), ("DOCKER_INFRA_CPUSET", "all") }, ""),
        ("whole existing cpuset", new[] { ("DOCKER_CPU", "all") }, "2-7,10-15")
    }) Check(label, () =>
    {
        var (config, _) = Driver([("CPU_TOPOLOGY_DIR", topology), .. values]).Render(withResources, image, 1);
        Equal(SequentialDriver.Text(config["resources"]!["cpuset"]), expected); Near(config["resources"]!["cpu"], 0);
        if (label == "all limits") Equal(config["resources"]!["infra_cpuset"], null);
    });
    foreach (var pair in new[] { ("DOCKER_CPU", "1.5"), ("DOCKER_CPUSET", "2-7;10") })
        Check($"reject CPU override {pair.Item1}", () => Reject(() => Driver(("CPU_TOPOLOGY_DIR", topology), pair).Render(withResources, image, 1)));
    await CheckAsync("cgroup readout stops on cancellation", async () =>
    {
        Equal(Driver().CpuOverridesActive(), false); Equal(Driver(("DOCKER_INFRA_CPUSET", "all")).CpuOverridesActive(), true);
        using var done = new CancellationTokenSource(); done.Cancel();
        var report = new List<string>(); await Driver().ReadCgroupLimits(report, done.Token); Equal(report.Count, 0);
    });
    foreach (var (name, failing, unclean, failFast, runs, expectedAttempts, expectedCode) in new[]
    {
        ("explicit comparison stops", "image-2", "", true, 2, "image-1-run1,image-1-run2,image-2-run1", 1),
        ("retrospective continues next image", "image-2", "", false, 2, "image-1-run1,image-1-run2,image-2-run1,image-3-run1,image-3-run2", 1),
        ("unclean sample stops retrospective", "", "image-2", false, 2, "image-1-run1,image-1-run2,image-2-run1", 1),
        ("clean campaign completes", "", "", true, 1, "image-1-run1,image-2-run1,image-3-run1", 0),
        ("summary failure retains campaign", "", "", true, 1, "image-1-run1,image-2-run1,image-3-run1", 1),
        ("final save failure retains samples", "", "", true, 1, "image-1-run1,image-2-run1,image-3-run1", 1)
    }) await CheckAsync(name, async () =>
    {
        var root = Path.Combine(temporary.FullName, name);
        var attempted = new List<string>();
        var driver = Driver(("EXPB_CAMPAIGN_DIR", root), ("RUN_COUNT", runs.ToString()), ("CAMPAIGN_FAIL_FAST", failFast ? "true" : "false"), ("EXPB_IMAGES_JSON", Images().ToJsonString()));
        var result = await driver.Run(CancellationToken.None, _ => Task.FromResult(Base()), (_, _) => Task.CompletedTask,
            (_, currentImage, run, _, _) =>
            {
                var id = currentImage["id"]!.ToString();
                attempted.Add($"{id}-run{run}");
                return Task.FromResult(Sample(id, run, id == failing || id == unclean ? "failed" : "success", 25, id != unclean));
            },
            name == "summary failure retains campaign" ? (_, _, _, _) => throw new IOException("summary denied") : null,
            name == "final save failure retains samples" ? (root, started, images, runs, samples, reasons) =>
            {
                if (reasons is not null) throw new IOException("not JSON serializable");
                SequentialDriver.SaveCampaign(root, started, images, runs, samples);
            } : null);
        Equal(result, expectedCode); Equal(string.Join(',', attempted), expectedAttempts);
        var campaign = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "campaign.json")))!;
        Equal(campaign["samples"]!.AsArray().Count, attempted.Count);
        if (name.Contains("failure", StringComparison.Ordinal))
            if (!campaign["failure_reasons"]!.ToJsonString().Contains("could not be written", StringComparison.Ordinal)) throw new Exception("Failure reason lost");
        if (name == "final save failure retains samples") Equal(campaign["status"]!.ToString(), "failed");
        if (name != "summary failure retains campaign") Equal(File.Exists(Path.Combine(root, "summary.md")), true);
    });
    foreach (var (images, key, value) in new[]
    {
        ("[]", "", ""), ("[{\"id\":\"..\",\"image\":\"repo:tag\"}]", "", ""), ("[{\"id\":\"a b\",\"image\":\"repo:tag\"}]", "", ""),
        (Images().ToJsonString(), "CLIENT_ENV", "FOO"), (Images().ToJsonString(), "EXPB_ENV_PASSTHROUGH", "bad-key=1"), (Images().ToJsonString(), "AMOUNT", "0"),
        (Images().ToJsonString(), "ADDITIONAL_EXTRA_FLAGS", "--JsonRpc.GasCap=100")
    }) await CheckAsync($"dispatch rejects {images} {key}", async () =>
    {
        var root = Path.Combine(temporary.FullName, "rejected");
        var values = new List<(string, string)> { ("EXPB_CAMPAIGN_DIR", root), ("EXPB_IMAGES_JSON", images), (key, value) };
        if (key == "ADDITIONAL_EXTRA_FLAGS") values.Add(("MEASUREMENT_MODE", "compute-warm"));
        var result = await Driver(values.ToArray()).Run(CancellationToken.None, _ => Task.FromResult(Base()), (_, _) => Task.CompletedTask,
            (_, _, _, _, _) => throw new Exception("No sample may run"));
        Equal(result, 1);
        Equal(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "campaign.json")))!["status"]!.ToString(), "failed");
    });
    Check("summary formats averages missing values and image mapping", () =>
    {
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "summary"));
        Driver().WriteSummary(root.FullName, new JsonArray((JsonNode)new JsonObject { ["id"] = "image-1", ["image"] = "repo:tag" }), 2,
            new JsonArray((JsonNode)Sample("image-1", 1, "success", 23.007412345678901), (JsonNode)Sample("image-1", 2, "failed", null)));
        var text = File.ReadAllText(Path.Combine(root.FullName, "summary.md"));
        foreach (var expected in new[] { "| image-1-run1 | success | SSE | 1 | 23.0074 | 23.0074 | n/a | n/a | n/a |", "| image-1-run2 | failed | SSE | 1 | n/a | n/a | n/a | n/a | n/a |", "- `image-1` → `repo:tag`", "Image image-1: mean AVG=23.0074 ms;" })
            if (!text.Contains(expected, StringComparison.Ordinal)) throw new Exception($"Missing summary: {expected}");
    });
    Check("comparison requires matching complete signatures", () =>
    {
        var first = Sample("a", 1, "success", 20); var second = Sample("b", 1, "success", 30);
        Equal(SequentialDriver.ComparisonDelta([first], [second], 1), 50d);
        Equal(SequentialDriver.ComparisonDelta([first], [second], 2), null);
        second["sse_block_ids"] = new JsonArray(2);
        Equal(SequentialDriver.ComparisonDelta([first], [second], 1), null);
        Equal(SequentialDriver.ImageStats([first, second]).Mean, null);
    });
    Check("sample gates delivery IDs shutdown cleanup diagnostics warmup cancellation", () =>
    {
        var (metrics, _) = Metrics(fullLog);
        Equal(Driver().FailureReasons(metrics, 2, 0, "", false).Count, 0);
        var cases = new (string Name, Action<JsonObject> Mutate)[]
        {
            ("duplicate delivery", value => value["payload_indices"] = new JsonArray(1L, 1L)),
            ("duplicate SSE", value => value["ids"] = new JsonArray(100L, 100L)),
            ("missing average", value => value["avg"] = null),
            ("missing shutdown", value => value["shutdown"] = false),
            ("missing cleanup", value => value["cleanup"] = false),
            ("exceptions", value => value["exception_count"] = 1),
            ("invalid blocks", value => value["invalid_count"] = 1)
        };
        foreach (var (name, mutate) in cases)
        {
            var changed = metrics.DeepClone().AsObject(); mutate(changed);
            if (Driver().FailureReasons(changed, 2, 0, "", false).Count == 0) throw new Exception($"Gate missed {name}");
        }
        Equal(Driver().FailureReasons(metrics, 2, 1, "unclean", true).Count, 3);
        Equal(Driver(("MEASUREMENT_MODE", "compute-warm")).FailureReasons(metrics, 2, 0, "", false).Count, 1);
        metrics["warm_ok"] = new JsonArray(1L, 2L);
        Equal(Driver(("MEASUREMENT_MODE", "compute-warm")).FailureReasons(metrics, 2, 0, "", false).Count, 0);
        metrics["shutdown"] = false;
        Equal(Driver(("CLIENT", "reth")).FailureReasons(metrics, 2, 0, "", false).Count, 0);
    });
    Check("cleanup rejects escaping nonempty and mounted scratch", () =>
    {
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "scratch"));
        var driver = Driver(("EXPB_DATA_DIR", root.FullName));
        driver.VerifyScratch(Base(), root.FullName, []);
        Reject(() => driver.VerifyScratch(Object("{\"paths\":{\"work\":\"../outside\"}}"), root.FullName, []));
        var upper = Directory.CreateDirectory(Path.Combine(root.FullName, "work", "upper")); File.WriteAllText(Path.Combine(upper.FullName, "debris"), "x");
        Reject(() => driver.VerifyScratch(Base(), root.FullName, []));
        Reject(() => driver.VerifyScratch(Base(), root.FullName, [$"overlay {root.FullName.Replace(" ", @"\040")} overlay rw 0 0"]));
    });
    if (!OperatingSystem.IsWindows()) Check("cleanup rejects symlink scratch", () =>
    {
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "linked scratch"));
        Directory.CreateDirectory(Path.Combine(root.FullName, "work"));
        Directory.CreateSymbolicLink(Path.Combine(root.FullName, "work", "upper"), temporary.FullName);
        Reject(() => Driver().VerifyScratch(Base(), root.FullName, []));
    });
    await CheckAsync("real sample logs environment private config and metadata", async () =>
    {
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "real sample"));
        var driver = Driver(("EXPB_BIN", Environment.ProcessPath!), ("EXPB_DATA_DIR", root.FullName), ("RUNNER_TEMP", root.FullName), ("AMOUNT", "1"),
            ("EXPB_ENV_PASSTHROUGH", "EXPB_TEST_VALUE=value with spaces,EXPB_TEST_STARTUP=26000000"), ("DOTTRACE", "true"), ("PERF", "true"),
            ("STATE_LAYOUT", "flat"), ("FLAT_SNAPSHOT_BLOCK_DIR", "/data/nethermind/nethermind-flat-26000000"));
        var config = Base(); config["export"] = new JsonObject { ["secret"] = "private" };
        config["scenarios"]!["nethermind"]!["snapshot_source"] = "/mnt/sda/nethermind-flat-25490000";
        var result = await driver.RunSample(config, image, 1, root.FullName, CancellationToken.None, (_, _) => Task.CompletedTask);
        Equal(result["status"]!.ToString(), "success"); Near(result["expected_amount"], 1);
        var artifact = File.ReadAllText(result["config"]!.ToString());
        if (artifact.Contains("private", StringComparison.Ordinal)) throw new Exception("Export credentials leaked");
        Equal(Directory.GetDirectories(root.FullName, ".expb-runtime-*").Length, 0);
        var log = File.ReadAllText(result["log"]!.ToString());
        if (!log.Contains("retained stderr", StringComparison.Ordinal)) throw new Exception("stderr lost");
        var command = result["command"]!.ToJsonString();
        foreach (var flag in new[] { "--dottrace", "--dotnet-trace", "--perf", "--startup-block", "26000000" }) if (!command.Contains(flag, StringComparison.Ordinal)) throw new Exception($"Missing {flag}");
        Equal(File.Exists(Path.Combine(root.FullName, "image-1-abc-run1", "metadata.json")), true);
    });
    if (OperatingSystem.IsLinux()) await CheckAsync("escaped descendant cannot hold log pipes forever", async () =>
    {
        var pidFile = Path.Combine(temporary.FullName, "escaped.pid");
        var log = Path.Combine(temporary.FullName, "escaped.log");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var code = await Driver().ExecuteLogged(["sh", "-c", "setsid sleep 60 & echo $! > \"$1\"; echo retained", "sh", pidFile], temporary.FullName, [], log, timeout.Token);
            Equal(code, 0); Equal(File.ReadAllText(log).Contains("retained", StringComparison.Ordinal), true);
        }
        finally
        {
            if (File.Exists(pidFile))
                try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))); child.Kill(); }
                catch (ArgumentException) { }
        }
    });
    foreach (var mode in OperatingSystem.IsLinux() ? new[] { "cancel", "kill", "orphan" } : ["cancel"])
    await CheckAsync($"real cancellation {mode} retains metadata and terminates child", async () =>
    {
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "cancel sample"));
        var pipeName = $"expb-test-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var driver = Driver(("EXPB_BIN", Environment.ProcessPath!), ("EXPB_DATA_DIR", root.FullName), ("RUNNER_TEMP", root.FullName), ("AMOUNT", "1"),
            ("CLEANUP_GRACE_SECONDS", mode == "kill" ? "0" : "2"), ("EXPB_ENV_PASSTHROUGH", $"EXPB_TEST_VALUE=value with spaces,EXPB_TEST_MODE={mode},EXPB_TEST_PIPE={pipeName}"));
        var config = Base(); config["export"] = new JsonObject { ["secret"] = "private" };
        var sample = driver.RunSample(config, image, 1, root.FullName, cancellation.Token, (_, _) => Task.CompletedTask);
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(pipe);
        var pid = int.Parse((await reader.ReadLineAsync(timeout.Token))!);
        cancellation.Cancel();
        JsonObject result;
        try { result = await sample.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            if (!sample.IsCompleted)
            {
                using var remaining = Process.GetProcessById(pid);
                remaining.Kill();
                await sample;
            }
        }
        Equal(result["status"]!.ToString(), "failed");
        if (!result["failure_reasons"]!.ToJsonString().Contains("campaign cancellation requested", StringComparison.Ordinal)) throw new Exception("Cancellation reason missing");
        if (OperatingSystem.IsLinux()) Equal(result["metrics"]!["cleanup"]!.GetValue<bool>(), mode == "cancel");
        try
        {
            using var child = Process.GetProcessById(pid);
            await child.WaitForExitAsync(timeout.Token);
            Equal(child.HasExited, true);
        }
        catch (ArgumentException) { }
    });
    Console.WriteLine($"Passed {tests} tests.");
    return 0;
}
finally { temporary.Delete(recursive: true); }

void Check(string name, Action test) { test(); Console.WriteLine($"PASS {name.Replace('\n', ' ')}"); tests++; }
async Task CheckAsync(string name, Func<Task> test) { await test(); Console.WriteLine($"PASS {name}"); tests++; }
static void Equal<T>(T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}");
}
static void Near(JsonNode? actual, double expected)
{
    if (actual is null || Math.Abs(double.Parse(actual.ToString(), System.Globalization.CultureInfo.InvariantCulture) - expected) > 1e-8)
        throw new Exception($"Expected {expected}, got {actual}");
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception exception) when (exception is ArgumentException or IOException) { return; }
    throw new Exception("Expected rejection");
}
static JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();
static JsonObject Base() => Object("""{"scenarios":{"nethermind":{"image":"placeholder"}}}""");
static SequentialDriver Driver(params (string Key, string Value)[] values)
{
    var environment = new Dictionary<string, string>();
    foreach (var (key, value) in values) environment[key] = value;
    return new SequentialDriver(key => environment.GetValueOrDefault(key));
}
(JsonObject Metrics, Dictionary<string, List<string>> Diagnostics) Metrics(string log, bool useSse = true)
{
    var path = Path.Combine(temporary.FullName, "combined.log"); File.WriteAllText(path, log);
    return SequentialDriver.CollectMetrics(path, useSse);
}
static JsonArray Images() => JsonNode.Parse("""[{"id":"image-1","image":"repo:tag1"},{"id":"image-2","image":"repo:tag2"},{"id":"image-3","image":"repo:tag3"}]""")!.AsArray();
static JsonObject Sample(string id, int run, string status, double? average, bool clean = true) => new()
{
    ["sample_id"] = $"{id}-run{run}", ["image_id"] = id, ["image"] = "repo:tag", ["run"] = run, ["status"] = status, ["cleanup_verified"] = clean,
    ["metrics"] = new JsonObject { ["source"] = "SSE", ["count"] = 1, ["avg"] = average, ["delivered"] = 1, ["ids"] = new JsonArray(1L),
        ["request"] = new JsonObject { ["avg"] = average }, ["outside"] = new JsonObject { ["avg"] = null }, ["mgas_s"] = null },
    ["sse_block_ids"] = new JsonArray(1L)
};

sealed class ProbeTimeProvider : TimeProvider
{
    private TimerCallback? _callback;
    private object? _state;
    internal TimeSpan DueTime { get; private set; } = Timeout.InfiniteTimeSpan;
    internal TimeSpan Period { get; private set; }
    internal bool TimerDisposed { get; private set; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _callback = callback;
        _state = state;
        DueTime = dueTime;
        Period = period;
        return new ProbeTimer(this);
    }
    internal void Expire() => _callback!(_state);
    private sealed class ProbeTimer(ProbeTimeProvider clock) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => !clock.TimerDisposed;
        public void Dispose() => clock.TimerDisposed = true;
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
