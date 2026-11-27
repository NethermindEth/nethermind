// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.Tools.Eip8288Bench;

/// <summary>Joins the load generator's <c>load.json</c> with each node's start/end resource and Prometheus snapshots written by
/// <c>scripts/eip8288-bench/mempool.sh</c>.</summary>
internal static class MempoolSummary
{
    private static readonly string[] WrapperEvents =
    [
        "announced", "announcement_received", "requested", "received", "validated", "invalid", "malformed", "published", "served",
        "request_expired", "announcement_suppressed", "reference_resolved", "reference_missed", "reference_sent", "equivalent_skipped",
        "budget_dropped"
    ];

    public static int Execute(Options options)
    {
        string dir = options.Path("dir", ".");
        int nodes = options.Int("nodes", 1);
        JsonNode load = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "load.json")))!;
        JsonNode? network = File.Exists(Path.Combine(dir, "network.json")) ? JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "network.json"))) : null;
        double seconds = load["offered"]!["seconds"]!.Num() + load["parameters"]!["graceSeconds"]!.Num();

        List<Dictionary<string, object?>> rows = [];
        StringBuilder csv = new("node,cpu_cores,memory_peak_mib,memory_end_mib,rx_kib_s,tx_kib_s,native_proofs,prove_ms,exceptions," + string.Join(',', WrapperEvents) + "\n");
        foreach (int node in Enumerable.Range(0, nodes))
        {
            Dictionary<string, double> start = Resources(Path.Combine(dir, $"node{node}.start.txt"));
            Dictionary<string, double> end = Resources(Path.Combine(dir, $"node{node}.end.txt"));
            Dictionary<string, double> promStart = Prometheus(Path.Combine(dir, $"node{node}.start.prom"));
            Dictionary<string, double> promEnd = Prometheus(Path.Combine(dir, $"node{node}.end.prom"));
            double Delta(Dictionary<string, double> a, Dictionary<string, double> b, string key) => b.GetValueOrDefault(key) - a.GetValueOrDefault(key);
            Dictionary<string, double> events = [];
            foreach (string name in WrapperEvents) events[name] = Delta(promStart, promEnd, $"nethermind_proof_wrapper_events{{event=\"{name}\"}}");
            int exceptions = File.Exists(Path.Combine(dir, $"node{node}.log"))
                ? File.ReadLines(Path.Combine(dir, $"node{node}.log")).Count(l => l.Contains("Exception", StringComparison.Ordinal)) : 0;
            Dictionary<string, object?> row = new()
            {
                ["node"] = node,
                ["cpuCores"] = Delta(start, end, "usage_usec") / 1e6 / seconds,
                ["memoryPeakMiB"] = end.GetValueOrDefault("memory_peak") / 1048576,
                ["memoryEndMiB"] = end.GetValueOrDefault("memory_current") / 1048576,
                ["rxKiBPerSecond"] = Delta(start, end, "eth0_rx") / 1024 / seconds,
                ["txKiBPerSecond"] = Delta(start, end, "eth0_tx") / 1024 / seconds,
                ["nativeProofs"] = Delta(promStart, promEnd, "nethermind_lean_native_proofs"),
                ["nativeProveMs"] = Delta(promStart, promEnd, "nethermind_lean_native_prove_milliseconds"),
                ["verificationCacheHits"] = Delta(promStart, promEnd, "nethermind_lean_verification_cache_hits"),
                ["pendingTransactionsEnd"] = promEnd.GetValueOrDefault("nethermind_transactions_pending"),
                ["exceptionLines"] = exceptions,
                ["wrapperEvents"] = events,
            };
            rows.Add(row);
            csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{node},{row["cpuCores"]:F3},{row["memoryPeakMiB"]:F0},{row["memoryEndMiB"]:F0},{row["rxKiBPerSecond"]:F1},{row["txKiBPerSecond"]:F1},{row["nativeProofs"]},{row["nativeProveMs"]},{exceptions},")
                + string.Join(',', WrapperEvents.Select(e => events[e].ToString(CultureInfo.InvariantCulture))));
        }

        double Mean(string key) => rows.Average(r => Convert.ToDouble(r[key], CultureInfo.InvariantCulture));
        double Max(string key) => rows.Max(r => Convert.ToDouble(r[key], CultureInfo.InvariantCulture));
        double EventSum(string name) => rows.Sum(r => ((Dictionary<string, double>)r["wrapperEvents"]!)[name]);
        double proofs = rows.Sum(r => Convert.ToDouble(r["nativeProofs"], CultureInfo.InvariantCulture));
        double proveMs = rows.Sum(r => Convert.ToDouble(r["nativeProveMs"], CultureInfo.InvariantCulture));
        double resolved = EventSum("reference_resolved"), missed = EventSum("reference_missed");
        double announcements = EventSum("announcement_received"), requested = EventSum("requested");
        Dictionary<string, object?> derived = new()
        {
            ["aggregationProofs"] = proofs,
            ["aggregationMsPerProof"] = proofs == 0 ? 0 : proveMs / proofs,
            ["aggregationCpuShare"] = proveMs / 1000 / seconds / nodes,
            ["fetchPerAnnouncement"] = announcements == 0 ? 0 : requested / announcements,
            ["referenceHitRate"] = resolved + missed == 0 ? double.NaN : resolved / (resolved + missed),
            ["suppressedAnnouncements"] = EventSum("announcement_suppressed"),
            ["equivalentSkipped"] = EventSum("equivalent_skipped"),
            ["mode1Published"] = EventSum("published"),
        };

        JsonNode propagation = load["propagationMs"]!;
        string label = network?["label"]?.GetValue<string>() ?? Path.GetFileName(dir);
        StringBuilder md = new();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Mempool `{label}`: {nodes} nodes x {network?["nodeCpus"]} vCPU / {network?["nodeMemory"]}, ~{network?["degree"]} peers, {network?["delayMs"]}±{network?["jitterMs"]} ms one-way, {network?["upMbit"]} Mbit/s up; offered {load["offered"]!["wrappersPerSecond"]!.Num():F1} wrappers/s for {load["offered"]!["seconds"]!.Num():F0} s."));
        md.AppendLine();
        md.AppendLine("| propagation p50 / p95 / p99 ms | coverage | accepted | node CPU cores mean / max | mem peak MiB max | rx / tx KiB/s mean | agg. proofs | ms/proof | fetch/announce | ref. hit rate | exception lines |");
        md.AppendLine("|---|---:|---:|---|---:|---|---:|---:|---:|---:|---:|");
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {propagation["p50"]!.Num():F0} / {propagation["p95"]!.Num():F0} / {propagation["p99"]!.Num():F0} | {load["coverage"]!.Num():P1} | {load["accepted"]}/{load["offered"]!["wrappers"]} | {Mean("cpuCores"):F2} / {Max("cpuCores"):F2} | {Max("memoryPeakMiB"):F0} | {Mean("rxKiBPerSecond"):F0} / {Mean("txKiBPerSecond"):F0} | {proofs:F0} | {derived["aggregationMsPerProof"]:F0} | {derived["fetchPerAnnouncement"]:F2} | {derived["referenceHitRate"]:F2} | {rows.Sum(r => (int)r["exceptionLines"]!)} |"));

        JsonObject result = new()
        {
            ["scenario"] = "mempool-propagation",
            ["network"] = network?.DeepClone(),
            ["load"] = load.DeepClone(),
            ["nodes"] = JsonSerializer.SerializeToNode(rows, Json.Options),
            ["derived"] = JsonSerializer.SerializeToNode(derived, Json.Options),
        };
        File.WriteAllText(Path.Combine(dir, "results.json"), result.ToJsonString(Json.Options));
        File.WriteAllText(Path.Combine(dir, "nodes.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(dir, "summary.md"), md.ToString());
        Console.WriteLine(md);
        return 0;
    }

    /// <summary>Across repeated runs of one arm: mean and coefficient of variation of each run's headline numbers.</summary>
    public static int Aggregate(Options options)
    {
        string[] dirs = options.String("dirs", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        string label = options.String("label", "runs");
        string[] keys = ["p50", "p95", "p99"];
        Dictionary<string, List<double>> values = [];
        void Add(string key, double value) => (values.TryGetValue(key, out List<double>? list) ? list : values[key] = []).Add(value);
        foreach (string dir in dirs)
        {
            JsonNode result = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "results.json")))!;
            foreach (string key in keys) Add("propagation " + key + " ms", result["load"]!["propagationMs"]![key].Num());
            Add("coverage", result["load"]!["coverage"].Num());
            JsonArray nodes = result["nodes"]!.AsArray();
            Add("node CPU cores", nodes.Average(n => n!["cpuCores"].Num()));
            Add("node rx KiB/s", nodes.Average(n => n!["rxKiBPerSecond"].Num()));
            Add("node tx KiB/s", nodes.Average(n => n!["txKiBPerSecond"].Num()));
            Add("node memory peak MiB", nodes.Max(n => n!["memoryPeakMiB"].Num()));
            Add("aggregation ms/proof", result["derived"]!["aggregationMsPerProof"].Num());
        }
        StringBuilder md = new($"Mempool `{label}` over {dirs.Length} runs (mean, CV across runs):\n\n| metric | mean | CV | min | max |\n|---|---:|---:|---:|---:|\n");
        Dictionary<string, Summary> summaries = [];
        foreach ((string key, List<double> list) in values)
        {
            Summary summary = summaries[key] = Summary.Of(list);
            md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {key} | {summary.Mean:F2} | {summary.Cv:P1} | {summary.Min:F2} | {summary.Max:F2} |"));
        }
        string outDir = options.Path("out", ".");
        File.WriteAllText(Path.Combine(outDir, "aggregate.json"), JsonSerializer.Serialize(new { label, dirs, summaries }, Json.Options));
        File.WriteAllText(Path.Combine(outDir, "aggregate.md"), md.ToString());
        Console.WriteLine(md);
        return 0;
    }

    /// <summary>A JSON number, or <c>NaN</c> written as a named literal.</summary>
    private static double Num(this JsonNode? node) =>
        node is null ? double.NaN : node.GetValueKind() == JsonValueKind.String ? double.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture) : node.GetValue<double>();

    /// <summary>Reads cgroup v2 <c>cpu.stat</c>, memory lines and <c>/proc/net/dev</c> output.</summary>
    private static Dictionary<string, double> Resources(string path)
    {
        Dictionary<string, double> values = [];
        if (!File.Exists(path)) return values;
        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("eth0:", StringComparison.Ordinal))
            {
                string[] fields = trimmed[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                values["eth0_rx"] = double.Parse(fields[0], CultureInfo.InvariantCulture);
                values["eth0_tx"] = double.Parse(fields[8], CultureInfo.InvariantCulture);
                continue;
            }
            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) values[parts[0]] = value;
        }
        return values;
    }

    /// <summary>Reads Prometheus text exposition into series name (with labels) to value.</summary>
    private static Dictionary<string, double> Prometheus(string path)
    {
        Dictionary<string, double> values = [];
        if (!File.Exists(path)) return values;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int split = line.LastIndexOf(' ');
            if (split <= 0 || !double.TryParse(line[(split + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
            string series = line[..split];
            // Keep only the event label so series match regardless of the instance labels Nethermind adds.
            int brace = series.IndexOf('{');
            if (brace > 0)
            {
                int eventLabel = series.IndexOf("event=\"", brace, StringComparison.Ordinal);
                series = eventLabel < 0 ? series[..brace]
                    : series[..brace] + "{" + series[eventLabel..(series.IndexOf('"', eventLabel + 7) + 1)] + "}";
            }
            values[series] = values.GetValueOrDefault(series) + value;
        }
        return values;
    }
}
