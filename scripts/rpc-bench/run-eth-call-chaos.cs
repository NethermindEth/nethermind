#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include benchmark-process.helpers.cs
#:include benchmark-paths.helpers.cs
#:include benchmark-files.helpers.cs

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
var container = Get("ECC_CONTAINER_NAME", "ethcallchaos-bench");
var launched = false;
try
{
    var output = Get("OUT_DIR"); var scratch = Get("SCRATCH_ROOT");
    if (output.Length == 0 || scratch.Length == 0) throw new ArgumentException("OUT_DIR and SCRATCH_ROOT are required");
    Directory.CreateDirectory(output);
    scratch = await BenchmarkPaths.CanonicalAsync(scratch, false, cancellation.Token); BenchmarkPaths.AssertSane(scratch, "SCRATCH_ROOT");
    var work = Path.Combine(scratch, "ethcallchaos");
    if (BenchmarkPaths.MountsUnder(await BenchmarkPaths.CanonicalAsync(work, false, cancellation.Token)).Length > 0) throw new IOException($"refusing to delete '{work}' — still mounted");
    var removed = await BenchmarkProcess.RootAsync("rm", ["-rf", "--", work], cancellation.Token);
    if (removed.Code != 0) throw new IOException("could not reset EthCallChaos scratch");
    Directory.CreateDirectory(work);
    var repository = Get("ECC_REPO", "https://github.com/kamilchodola/EthCallChaos.git"); var revision = Get("ECC_REF", "v1.0.0");
    var source = Path.Combine(work, "src");
    BenchmarkProcess.Log($"Cloning {repository}@{revision}...");
    await Required("git", ["init", "-q", source]); await Required("git", ["-C", source, "remote", "add", "origin", repository]);
    await Required("git", ["-C", source, "fetch", "-q", "--depth", "1", "origin", revision]); await Required("git", ["-C", source, "checkout", "-q", "FETCH_HEAD"]);
    var project = Path.Combine(source, "src", "EthCallChaos");
    if (!Directory.Exists(project)) throw new IOException($"EthCallChaos project not found at {project}");
    var database = Path.Combine(work, "bench.db");
    var corpus = Get("ECC_CORPUS_DB"); var corpusUrl = Get("ECC_CORPUS_URL", "https://github.com/kamilchodola/EthCallChaos/releases/download/corpus-v2/ethcallchaos.db");
    if (corpus.Length > 0 && File.Exists(corpus)) { File.Copy(corpus, database); BenchmarkProcess.Log($"Using provided corpus DB (copied): {corpus}"); }
    else if (corpusUrl.Length > 0 && (await BenchmarkProcess.RunAsync("curl", ["-sfL", "--retry", "3", "-o", database, corpusUrl], cancellation.Token)).Code == 0)
    {
        var checksum = Get("ECC_CORPUS_SHA256");
        if (checksum.Length > 0)
        {
            await using var stream = File.OpenRead(database);
            if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation.Token)).Equals(checksum, StringComparison.OrdinalIgnoreCase)) throw new IOException("corpus DB sha256 mismatch");
        }
        BenchmarkProcess.Log($"Using corpus DB downloaded from {corpusUrl} ({new FileInfo(database).Length} bytes).");
    }
    else if (File.Exists(Path.Combine(project, "ethcallchaos.db"))) { File.Copy(Path.Combine(project, "ethcallchaos.db"), database, overwrite: true); BenchmarkProcess.Log("Using corpus DB committed in the repo (copied)."); }
    else { File.WriteAllText(database, ""); BenchmarkProcess.Log("::warning::No corpus DB found — EthCallChaos starts from a fresh corpus."); }
    var url = Get("RPC_URL", "http://localhost:8545"); var rate = Get("ECC_RATE", "50"); var parallel = Get("ECC_PARALLEL", "8"); var duration = Get("ECC_DURATION", "300"); var port = Get("ECC_API_PORT", "5000");
    await BenchmarkProcess.RunAsync("docker", ["rm", "-f", container], cancellation.Token);
    var arguments = new List<string> { "run", "-d", "--name", container, "--network", "host", "-v", work + ":/work", "-w", "/work/src/src/EthCallChaos" };
    foreach (var value in new[] { "ASPNETCORE_ENVIRONMENT=Production", "ASPNETCORE_URLS=http://127.0.0.1:" + port, "Kestrel__Endpoints__Http__Url=http://127.0.0.1:" + port, "DOTNET_CLI_HOME=/work", "NUGET_PACKAGES=/work/.nuget", "Rpc__NodeUrl=" + url, "Rpc__MaxCallsPerSecond=" + rate, "Rpc__MaxParallelCalls=" + parallel, "Validation__MinMeanThresholdMs=" + Get("ECC_MIN_MEAN_MS", "1"), "Validation__MaxCoefficientOfVariation=" + Get("ECC_MAX_CV", "10"), "ConnectionStrings__Sqlite=Data Source=/work/bench.db", "Storage__ConnectionString=Data Source=/work/bench.db" }) arguments.AddRange(["-e", value]);
    arguments.AddRange([Get("SDK_IMAGE", "mcr.microsoft.com/dotnet/sdk:10.0"), "bash", "-lc", "dotnet run -c Release"]);
    launched = true;
    await Required("docker", arguments);
    var api = "http://localhost:" + port;
    var elapsed = 0;
    while ((await BenchmarkProcess.RunAsync("curl", ["-sf", api + "/api/stats"], cancellation.Token)).Code != 0)
    {
        var listing = await BenchmarkProcess.RunAsync("docker", ["ps", "--format", "{{.Names}}"], cancellation.Token);
        if (!listing.Output.Split('\n').Select(line => line.TrimEnd('\r')).Contains(container, StringComparer.Ordinal)) await FailStartup("EthCallChaos container exited before the API came up");
        await Task.Delay(5000, cancellation.Token); elapsed += 5;
        if (elapsed >= 900) await FailStartup("EthCallChaos API never came up within 900s");
    }
    BenchmarkProcess.Log($"API up after {elapsed}s. Generating load for {duration}s...");
    await Task.Delay(TimeSpan.FromSeconds(double.Parse(duration, CultureInfo.InvariantCulture)), cancellation.Token);
    var top = Get("ECC_LEADERBOARD_TOP", "50"); var failed = false;
    foreach (var (endpoint, name) in new[] { ("/api/stats", "stats.json"), ($"/api/leaderboard?top={top}&sortBy=mean_ms", "leaderboard.json") })
        if ((await BenchmarkProcess.RunAsync("curl", ["-sf", api + endpoint, "-o", Path.Combine(output, name)], cancellation.Token)).Code != 0) { failed = true; BenchmarkProcess.Log($"::warning::failed to scrape {endpoint}"); }
    await BenchmarkFiles.RunCombinedToFileAsync("docker", ["logs", container], Path.Combine(output, "ethcallchaos.log"), cancellation.Token);
    await BenchmarkProcess.RunAsync("docker", ["stop", "-t", "20", container], cancellation.Token);
    if (File.Exists(database) && new FileInfo(database).Length > 0)
        try { File.Copy(database, Path.Combine(output, "ethcallchaos.db"), overwrite: true); }
        catch (IOException exception) { BenchmarkProcess.Log($"::warning::failed to persist evolved corpus DB: {exception.Message}"); }
    await BenchmarkProcess.RunAsync("docker", ["rm", "-f", container], cancellation.Token); launched = false;
    var summary = new StringBuilder($"## RPC Benchmark — EthCallChaos\n\nNode: `{url}` | rate: `{rate}/s` | parallel: `{parallel}` | duration: `{duration}s`\n\n");
    var statsPath = Path.Combine(output, "stats.json"); var leaderboardPath = Path.Combine(output, "leaderboard.json");
    if (File.Exists(statsPath) && new FileInfo(statsPath).Length > 0)
    {
        var stats = File.ReadAllText(statsPath);
        try { stats = JsonNode.Parse(stats)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null"; }
        catch (JsonException) { failed = true; }
        summary.Append("### Stats\n```json\n").Append(stats).Append("\n```\n\n");
    }
    if (File.Exists(leaderboardPath) && new FileInfo(leaderboardPath).Length > 0)
    {
        summary.Append($"### Slowest eth_call cases (top {top}, by mean ms)\n\n| Rank | mean ms | p99 ms | to | calldata |\n|---:|---:|---:|---|---|\n");
        try
        {
            var leaderboard = JsonNode.Parse(File.ReadAllText(leaderboardPath)) as JsonArray;
            if (leaderboard is not null)
                foreach (var row in leaderboard.Take(int.Parse(top, CultureInfo.InvariantCulture)))
                {
                    var cells = new[] { ("rankPosition", "RankPosition"), ("meanMs", "MeanMs"), ("p99Ms", "P99Ms"), ("toAddress", "ToAddress"), ("calldataPreview", "CalldataPreview") }.Select(pair => (row?[pair.Item1] ?? row?[pair.Item2])?.ToString() ?? "-");
                    summary.Append("| ").AppendJoin(" | ", cells).Append(" |\n");
                }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { failed = true; }
    }
    await File.WriteAllTextAsync(Path.Combine(output, "ethcallchaos-summary.md"), summary.ToString(), cancellation.Token);
    if (failed) throw new IOException("EthCallChaos results could not be scraped (container log is in the artifact)");
    foreach (var path in new[] { statsPath, leaderboardPath })
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.False) throw new IOException("EthCallChaos response is empty or not valid JSON");
    }
    return 0;
    async Task FailStartup(string message)
    {
        var log = Path.Combine(output, "ethcallchaos.log");
        await BenchmarkFiles.RunCombinedToFileAsync("docker", ["logs", container], log, cancellation.Token);
        foreach (var line in File.ReadLines(log).TakeLast(120)) Console.WriteLine(line);
        throw new IOException(message);
    }
    async Task Required(string executable, IEnumerable<string> arguments)
    {
        var result = await BenchmarkProcess.RunAsync(executable, arguments, cancellation.Token); Console.Write(result.Output); Console.Error.Write(result.Error);
        if (result.Code != 0) throw new IOException($"{executable} failed with exit code {result.Code}");
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("EthCallChaos cancelled."); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or JsonException or System.ComponentModel.Win32Exception)
{ Console.Error.WriteLine($"ERROR: {exception.Message}"); return 1; }
finally
{
    Console.CancelKeyPress -= cancel;
    if (launched)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await BenchmarkProcess.RunAsync("docker", ["rm", "-f", container], cleanup.Token); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.ComponentModel.Win32Exception) { Console.Error.WriteLine($"EthCallChaos cleanup failed: {exception.Message}"); }
    }
}
static string Get(string name, string fallback = "") => BenchmarkProcess.EnvironmentValue(name, fallback);
