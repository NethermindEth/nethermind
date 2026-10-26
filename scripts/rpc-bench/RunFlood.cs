#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include BenchmarkProcess.Helpers.cs
#:include BenchmarkFiles.Helpers.cs
#:include BenchmarkRpc.Helpers.cs
#:include NodeConfiguration.Helpers.cs

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var cancellation = new CancellationTokenSource();
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    var output = Get("OUT_DIR"); if (output.Length == 0) throw new ArgumentException("OUT_DIR is required");
    Directory.CreateDirectory(output);
    var url = Get("RPC_URL", "http://localhost:8545"); var label = Get("LABEL", "nethermind");
    var reference = Get("REFERENCE_RPC_URL"); var referenceLabel = Get("REFERENCE_LABEL", "reference");
    if (reference.Length > 0 && referenceLabel == label) referenceLabel += "-ref";
    var rates = Get("RATES", "10 100 500"); var duration = Get("DURATION", "30"); var deep = Get("DEEP_CHECK", "false");
    var localBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
    Environment.SetEnvironmentVariable("PATH", localBin + Path.PathSeparator + Get("PATH"));
    if (!Exists("vegeta"))
    {
        var version = Get("VEGETA_VERSION", "12.11.1");
        var temporary = Directory.CreateTempSubdirectory("rpc-vegeta-");
        try
        {
            var archive = Path.Combine(temporary.FullName, "vegeta.tgz");
            await Required("curl", ["-sSfL", $"https://github.com/tsenart/vegeta/releases/download/v{version}/vegeta_{version}_linux_amd64.tar.gz", "-o", archive]);
            await using (var stream = File.OpenRead(archive))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation.Token)).Equals(Get("VEGETA_SHA256", "1dbdb525fe82e084626e02e73405eb386a3ed1a894426e22f440f6565b3e5d17"), StringComparison.OrdinalIgnoreCase)) throw new IOException("vegeta tarball sha256 mismatch");
            await Required("tar", ["-xzf", archive, "-C", temporary.FullName, "vegeta"]);
            if ((await BenchmarkProcess.RootAsync("install", ["-m0755", Path.Combine(temporary.FullName, "vegeta"), "/usr/local/bin/vegeta"], cancellation.Token)).Code != 0)
            {
                Directory.CreateDirectory(localBin); await Required("install", ["-m0755", Path.Combine(temporary.FullName, "vegeta"), Path.Combine(localBin, "vegeta")]);
            }
        }
        finally { temporary.Delete(recursive: true); }
    }
    var vegeta = await BenchmarkProcess.RunAsync("vegeta", ["--version"], cancellation.Token); Console.Write(vegeta.Output); Console.Error.Write(vegeta.Error);
    var repository = Get("FLOOD_REPO", "git+https://github.com/kamilchodola/flood.git");
    var revision = Get("FLOOD_REF", "bd0d8e4e3d698cf5b5f141c2a36d86f5f5b5e1ef");
    var spec = revision.Length > 0 && !repository.Contains('@') ? repository + "@" + revision : repository;
    BenchmarkProcess.Log($"Installing flood from {spec}...");
    if (Exists("uv"))
    {
        if ((await BenchmarkProcess.RunAsync("uv", ["tool", "install", "--force", "--python", "3.10", "--with", "lxml-html-clean", spec], cancellation.Token)).Code != 0)
            await Required("uv", ["tool", "install", "--force", "--python", "3.11", "--with", "lxml-html-clean", spec]);
        var bin = await BenchmarkProcess.RunAsync("uv", ["tool", "dir", "--bin"], cancellation.Token);
        if (bin.Code != 0) throw new IOException("could not resolve uv tool directory");
        Environment.SetEnvironmentVariable("PATH", bin.Output.Trim() + Path.PathSeparator + Get("PATH"));
    }
    else if ((await BenchmarkProcess.RunAsync("python3", ["-m", "pip", "install", "--user", "--force-reinstall", spec], cancellation.Token)).Code != 0)
        await Required("python3", ["-m", "pip", "install", "--user", "--break-system-packages", "--force-reinstall", spec]);
    if (!Exists("flood")) throw new IOException("flood not on PATH after install");
    string[] tests;
    if (Get("TESTS").Length > 0) tests = Get("TESTS").Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
    else if (reference.Length > 0) tests = ["all"];
    else
    {
        var listing = await BenchmarkProcess.RunAsync("flood", ["ls"], cancellation.Token);
        var active = false; var names = new List<string>();
        foreach (var line in listing.Output.Split('\n'))
        {
            if (line.Contains("Single Load Tests", StringComparison.Ordinal)) { active = true; continue; }
            if (line.Contains("Multi Load Tests", StringComparison.Ordinal)) { active = false; continue; }
            if (active && !line.Contains("───", StringComparison.Ordinal) && line.Contains("- ", StringComparison.Ordinal)) names.Add(WhitespaceRegex().Replace(line.Replace("- ", "", StringComparison.Ordinal), ""));
        }
        tests = names.Where(name => name.Length > 0).ToArray();
    }
    if (tests.Length == 0) throw new IOException($"no flood tests resolved (filter='{Get("TESTS")}')");
    BenchmarkProcess.Log($"Will run {tests.Length} flood test(s): {string.Join(' ', tests)}");
    if (reference.Length > 0)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var a = await BenchmarkRpc.HeadAsync(http, url, cancellation.Token); var b = await BenchmarkRpc.HeadAsync(http, reference, cancellation.Token);
        if (a is null || b is null) throw new IOException("assert_same_head: could not read eth_blockNumber from both nodes");
        if (Convert.ToInt64(a[2..], 16) != Convert.ToInt64(b[2..], 16)) throw new IOException($"head mismatch: {url} is at {a} but {reference} is at {b}");
    }
    var invocationFailures = 0;
    foreach (var test in tests)
    {
        var arguments = new List<string> { test, label + "=" + url };
        if (reference.Length > 0) arguments.AddRange([referenceLabel + "=" + reference, "--equality"]);
        else
        {
            arguments.Add("--rates"); arguments.AddRange(NodeConfiguration.SplitFlags(rates)); arguments.AddRange(["--duration", duration]);
            if (deep == "true") arguments.Add("--deep-check"); arguments.AddRange(["--output", Path.Combine(output, test)]);
        }
        arguments.AddRange(NodeConfiguration.SplitFlags(Get("EXTRA_ARGS")));
        var code = await BenchmarkFiles.RunCombinedToFileAsync("flood", arguments, Path.Combine(output, test + ".log"), cancellation.Token, echo: true);
        if (code != 0) { invocationFailures++; BenchmarkProcess.Log($"::warning::flood test '{test}' exited non-zero (continuing)"); }
    }
    var text = new StringBuilder(reference.Length > 0 ? $"## RPC Comparison — flood equality (differential test)\n\n`{label}` = `{url}` vs `{referenceLabel}` = `{reference}`\n\n" : $"## RPC Benchmark — flood (Vegeta load test)\n\nNode: `{url}` | rates: `{rates}` req/s | duration: `{duration}s` | deep-check: `{deep}`\n\n");
    var missing = 0; var malformed = 0;
    foreach (var test in tests)
    {
        text.Append($"### {test}\n\n");
        var path = Path.Combine(output, test + ".log");
        if (reference.Length > 0)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) { text.Append("**NO OUTPUT** — flood wrote no log for this test.\n"); missing++; }
            else
            {
                var lines = new Queue<string>(); var differences = false;
                foreach (var raw in File.ReadLines(path))
                {
                    var line = AnsiRegex().Replace(raw, "");
                    differences |= DifferenceRegex().IsMatch(line);
                    lines.Enqueue(line); if (lines.Count > 120) lines.Dequeue();
                }
                if (differences) text.Append($"> :warning: **response differences detected** — see `{test}.log` in the artifact.\n\n");
                text.Append("<details><summary>flood output (last 120 lines)</summary>\n\n```\n").AppendJoin('\n', lines).Append("\n```\n\n</details>\n");
            }
        }
        else
        {
            path = Path.Combine(output, test, "results.json");
            if (!File.Exists(path)) { text.Append($"**NO RESULTS** — flood did not write `results.json` (see `{test}.log` in the artifact).\n"); missing++; }
            else
            {
                text.Append("| node | rate (rps) | actual rate | success | mean (ms) | p50 (ms) | p90 (ms) | p99 (ms) | max (ms) | requests |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
                try
                {
                    var results = JsonNode.Parse(File.ReadAllText(path))!["results"]!.AsObject();
                    foreach (var (node, row) in results)
                        for (var index = 0; index < row!["target_rate"]!.AsArray().Count; index++)
                        {
                            double Value(string key) => row[key]![index]!.GetValue<double>();
                            var cells = new List<string> { node, Value("target_rate").ToString(CultureInfo.InvariantCulture), (Math.Floor(Value("actual_rate") * 100 + .5) / 100).ToString(CultureInfo.InvariantCulture), Math.Floor(Value("success") * 100 + .5) + "%" };
                            cells.AddRange(new[] { "mean", "p50", "p90", "p99", "max" }.Select(key => (Math.Floor(Value(key) * 100000 + .5) / 100).ToString(CultureInfo.InvariantCulture)));
                            cells.Add(Value("requests").ToString(CultureInfo.InvariantCulture)); text.Append("| ").AppendJoin(" | ", cells).Append(" |\n");
                        }
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or FormatException)
                { malformed++; text.Append($"\nFailed to render {path}\n"); }
            }
        }
        text.Append('\n');
    }
    var summary = Path.Combine(output, "flood-summary.md"); await File.WriteAllTextAsync(summary, text.ToString(), cancellation.Token);
    BenchmarkProcess.Log($"flood summary written to {summary}");
    if (invocationFailures > 0) BenchmarkProcess.Log($"::warning::{invocationFailures} flood invocation(s) exited non-zero — results completeness gates the step");
    if (missing > 0 || malformed > 0) throw new IOException($"flood benchmark incomplete: {missing} test(s) produced no output/results; {malformed} results.json failed to render");
    return 0;

    async Task Required(string executable, IEnumerable<string> arguments)
    {
        var result = await BenchmarkProcess.RunAsync(executable, arguments, cancellation.Token); Console.Write(result.Output); Console.Error.Write(result.Error);
        if (result.Code != 0) throw new IOException($"{executable} failed with exit code {result.Code}");
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("Flood cancelled."); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
{ Console.Error.WriteLine($"ERROR: {exception.Message}"); return 1; }
finally { Console.CancelKeyPress -= cancel; }

static string Get(string name, string fallback = "") => BenchmarkProcess.EnvironmentValue(name, fallback);
static bool Exists(string command) => Get("PATH").Split(Path.PathSeparator).Any(directory => File.Exists(Path.Combine(directory, command)));

partial class Program
{
    [GeneratedRegex(@"\s")]
    private static partial Regex WhitespaceRegex();
    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();
    [GeneratedRegex("mismatch|not equal|✖|✗|FAILED", RegexOptions.IgnoreCase)]
    private static partial Regex DifferenceRegex();
}
