// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class TraceStorePreflight
{
    private const string ImageDigest = "@sha256:44af6def5be993e58e23df6b43eca490091c487aeac863c2d3d611234dbf5431";
    private readonly JsonNode _manifest;
    private readonly JsonNode _target;
    private readonly string[] _transactions;
    private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);

    internal TraceStorePreflight(string manifest)
    {
        _manifest = JsonNode.Parse(File.ReadAllText(manifest), documentOptions: RpcJson.DocumentOptions)
            ?? throw new IOException("Missing TraceStore manifest.");
        Require(_manifest["blocks"] is JsonArray { Count: 1 }, "TraceStore manifest must contain one fixed block.");
        _target = _manifest["blocks"]![0]!;
        _transactions = _target["transaction_hashes"]!.AsArray().Select(hash => hash!.GetValue<string>()).ToArray();
        Require(_manifest["snapshot_block"]!.GetValue<long>() == 25490000 &&
            _target["number"]!.GetValue<long>() == 25489981 && _transactions.Length == 263,
            "Unexpected TraceStore experiment fixture.");
    }

    internal static void ValidateExperiment(SweepArm[] arms, long[] rates, string duration, string isolated, string workload)
    {
        static string WithoutStore(SweepArm arm) => string.Join(' ', arm.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(flag => !flag.StartsWith("--TraceStore.Enabled=", StringComparison.Ordinal)));
        Require(arms[0].Image == arms[1].Image && arms[0].Environment == arms[1].Environment && WithoutStore(arms[0]) == WithoutStore(arms[1]), "TraceStore arms differ in more than Enabled.");
        Require(arms.Select(arm => arm.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Single(flag => flag.StartsWith("--TraceStore.Enabled=", StringComparison.Ordinal))).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["--TraceStore.Enabled=false", "--TraceStore.Enabled=true"]), "Expected one OFF and one ON arm.");
        Require(rates.SequenceEqual([3L]) && duration == "120s" && isolated.Length == 0, "Unexpected TraceStore timing cell settings.");
        var config = JsonNode.Parse(File.ReadAllText(workload))!;
        var calls = config["calls"]!.AsArray();
        Require(config["vus"]!.GetValue<int>() == 1 && calls.Count == 2, "Unexpected TraceStore workload shape.");
        var expected = JsonNode.Parse("""[{"method":"debug_traceBlockByNumber","params":["0x184f23d",{"tracer":"callTracer"}]},{"method":"trace_block","params":["0x184f23d"]}]""")!.AsArray();
        for (var index = 0; index < calls.Count; index++)
            Require(calls[index]!["file"] is null && JsonNode.DeepEquals(calls[index]!["method"], expected[index]!["method"]) &&
                JsonNode.DeepEquals(calls[index]!["params"], expected[index]!["params"]), "TraceStore measured requests differ from the fixed preflight requests.");
    }

    internal async Task ValidateAsync(SweepArm arm, string container, string output, CancellationToken cancellationToken)
    {
        Require(arm.Image.EndsWith(ImageDigest, StringComparison.Ordinal), "TraceStore image must be the pinned master image.");
        var enabled = arm.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Single(flag => flag.StartsWith("--TraceStore.Enabled=", StringComparison.Ordinal)).Split('=')[1];
        Require(enabled is "true" or "false", "Missing explicit TraceStore arm value.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "arm.json"), new JsonObject
        {
            ["Client"] = arm.Client, ["Image"] = arm.Image, ["Environment"] = arm.Environment,
            ["Flags"] = arm.Flags, ["Label"] = arm.Label
        }.ToJsonString(RpcJson.SerializerOptions));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var settled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        settled.CancelAfter(TimeSpan.FromMinutes(15));
        JsonNode head;
        do
        {
            head = await Call(http, output, "head", "eth_blockNumber", [], settled.Token);
            if (Number(head) == 25490000) break;
            Require(Number(head) < 25490000, "Snapshot advanced past the pinned head.");
            await Task.Delay(1000, settled.Token);
        } while (true);
        Require(Number(await Call(http, output, "chain", "eth_chainId", [], cancellationToken)) == 1, "Wrong chain.");
        var version = await Call(http, output, "version", "web3_clientVersion", [], cancellationToken);
        Require(version.GetValue<string>().Contains("57023d1", StringComparison.OrdinalIgnoreCase), "Wrong runtime source commit.");
        await ValidateConfig(http, output, "TraceStore", "Enabled", enabled, cancellationToken);
        await ValidateConfig(http, output, "TraceStore", "BlocksToKeep", "20000", cancellationToken);
        await ValidateConfig(http, output, "TraceStore", "TraceTypes", "Trace,Rewards", cancellationToken);
        await ValidateConfig(http, output, "JsonRpc", "TraceBlockParallelism", "1", cancellationToken);
        await ValidateConfig(http, output, "FlatDb", "HistoryTransactionIndexTraceParallelism", "0", cancellationToken);
        await ValidateConfig(http, output, "Receipt", "StoreTxHashes", "false", cancellationToken);
        var snapshot = await Call(http, output, "snapshot", "eth_getBlockByNumber", new JsonArray("0x184f250", false), cancellationToken);
        Require(Number(snapshot["number"]!) == 25490000 && snapshot["hash"]!.GetValue<string>() == _manifest["snapshot_hash"]!.GetValue<string>(), "Snapshot hash mismatch.");
        var block = await Call(http, output, "block", "eth_getBlockByNumber", new JsonArray(_target["hex_number"]!.GetValue<string>(), false), cancellationToken);
        Require(Number(block["number"]!) == 25489981 && block["hash"]!.GetValue<string>() == _target["hash"]!.GetValue<string>(), "Target block hash mismatch.");
        Require(block["transactions"]!.AsArray().Select(hash => hash!.GetValue<string>()).SequenceEqual(_transactions), "Target transaction count/order mismatch.");
        var debug = await Call(http, output, "debug-trace", "debug_traceBlockByNumber", new JsonArray(_target["hex_number"]!.GetValue<string>(), new JsonObject { ["tracer"] = "callTracer" }), cancellationToken);
        ValidateResult("debug_traceBlockByNumber", debug);
        var before = enabled == "true" ? await HitLines(container, cancellationToken) : Array.Empty<string>();
        var trace = await Call(http, output, "parity-trace", "trace_block", new JsonArray(_target["hex_number"]!.GetValue<string>()), cancellationToken);
        ValidateResult("trace_block", trace);
        if (enabled == "true")
        {
            var after = await HitLines(container, cancellationToken);
            for (var attempt = 0; after.Length <= before.Length && attempt < 10; attempt++)
            {
                await Task.Delay(500, cancellationToken);
                after = await HitLines(container, cancellationToken);
            }
            File.WriteAllText(Path.Combine(output, "store-hit-proof.json"), new JsonObject
            {
                ["before"] = before.Length, ["after"] = after.Length,
                ["newLines"] = new JsonArray(after.Skip(before.Length).Select(line => (JsonNode?)JsonValue.Create(line)).ToArray())
            }.ToJsonString(RpcJson.SerializerOptions));
            Require(after.Length > before.Length, "TraceStore ON missed the exact fixed block; timing is refused.");
        }
        File.WriteAllText(Path.Combine(output, "passed.txt"), "Preflight passed. ON uses narrow hit logging. Full result arrays match all prior arms.\n");
    }

    private static async Task ValidateConfig(HttpClient http, string output, string category, string name, string expected, CancellationToken cancellationToken)
    {
        var value = await Call(http, output, "config-" + category + "-" + name, "debug_getConfigValue", new JsonArray(category, name), cancellationToken);
        Require(value.ToString().Replace(" ", "", StringComparison.Ordinal).Equals(expected, StringComparison.OrdinalIgnoreCase), "Unexpected runtime config: " + category + "." + name);
    }

    private async Task<string[]> HitLines(string container, CancellationToken cancellationToken)
    {
        var logs = await BenchmarkProcess.RunAsync("docker", ["logs", container], cancellationToken);
        Require(logs.Code == 0, "Could not inspect TraceStore hit logs.");
        return (logs.Output + logs.Error).Split('\n').Where(line => line.Contains("Found persisted traces for block", StringComparison.Ordinal) &&
            line.Contains("25489981", StringComparison.Ordinal) && line.Contains(_target["hash"]!.GetValue<string>(), StringComparison.Ordinal)).ToArray();
    }

    private static async Task<JsonNode> Call(HttpClient http, string output, string name, string method, JsonArray parameters, CancellationToken cancellationToken)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters };
        using var content = new StringContent(request.ToJsonString(RpcJson.SerializerOptions), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("http://localhost:8545", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        File.WriteAllText(Path.Combine(output, name + ".json"), text);
        var envelope = JsonNode.Parse(text, documentOptions: RpcJson.DocumentOptions);
        return Result(envelope);
    }

    private static JsonNode Result(JsonNode? envelope)
    {
        Require(envelope is JsonObject && envelope["jsonrpc"]?.GetValue<string>() == "2.0" &&
            envelope["error"] is null && envelope["result"] is not null && !envelope.AsObject().Any(pair => pair.Key.StartsWith('_')),
            "Missing, truncated, or failed full JSON-RPC response.");
        return envelope!["result"]!;
    }

    internal void ValidateCapture(string path)
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var row = JsonNode.Parse(line, documentOptions: RpcJson.DocumentOptions)!;
            var method = row["method"]!.GetValue<string>();
            Require(methods.Add(method), "Duplicate final trace capture method.");
            ValidateResult(method, Result(row["response"]));
        }
        Require(methods.SetEquals(["debug_traceBlockByNumber", "trace_block"]), "Final trace captures are incomplete.");
    }

    private void ValidateResult(string method, JsonNode result)
    {
        Require(result is JsonArray { Count: > 0 }, "Trace result is empty.");
        var entries = result.AsArray();
        if (method == "debug_traceBlockByNumber")
        {
            Require(entries.Count == _transactions.Length && entries.All(entry => entry is JsonObject && entry["error"] is null && entry["result"] is JsonObject { Count: > 0 }), "Incomplete native block traces.");
            Require(entries.Select(entry => entry!["txHash"]!.GetValue<string>()).SequenceEqual(_transactions), "Native trace transaction order mismatch.");
        }
        else
        {
            Require(method == "trace_block", "Unexpected trace method.");
            var roots = new List<string>();
            var previousPosition = -1L;
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            var trees = new Dictionary<long, List<(int[] Path, int Children)>>();
            foreach (var entry in entries)
            {
                Require(entry is JsonObject && entry["blockHash"]!.GetValue<string>() == _target["hash"]!.GetValue<string>() && Number(entry["blockNumber"]!) == 25489981, "Parity trace block mismatch.");
                var position = Number(entry!["transactionPosition"]!);
                Require(position >= previousPosition && position >= 0 && position < _transactions.Length && entry["transactionHash"]!.GetValue<string>() == _transactions[position], "Parity transaction order/hash mismatch.");
                var address = entry["traceAddress"]!.AsArray();
                Require(address.All(index => index!.GetValue<int>() >= 0) && addresses.Add(position + ":" + address.ToJsonString()), "Duplicate/invalid parity traceAddress.");
                if (!trees.TryGetValue(position, out var tree)) trees.Add(position, tree = []);
                var path = address.Select(index => index!.GetValue<int>()).ToArray();
                Require(path.Length == 0 || tree.Any(parent => path.AsSpan(0, path.Length - 1).SequenceEqual(parent.Path)), "Parity trace parent is missing or unordered.");
                if (tree.Count > 0) Require(ComparePath(tree[^1].Path, path) < 0, "Parity trace traversal order mismatch.");
                var children = entry["subtraces"]!.GetValue<int>();
                Require(children >= 0, "Invalid parity subtraces count.");
                tree.Add((path, children));
                if (address.Count == 0) roots.Add(entry["transactionHash"]!.GetValue<string>());
                previousPosition = position;
            }
            Require(roots.SequenceEqual(_transactions), "Parity trace roots are incomplete or unordered.");
            foreach (var tree in trees.Values)
                foreach (var (path, children) in tree)
                {
                    var indices = tree.Where(child => child.Path.Length == path.Length + 1 && child.Path.AsSpan(0, path.Length).SequenceEqual(path)).Select(child => child.Path[^1]);
                    Require(indices.SequenceEqual(Enumerable.Range(0, children)), "Parity trace descendants are incomplete.");
                }
        }
        using var document = JsonDocument.Parse(result.ToJsonString(RpcJson.SerializerOptions), RpcJson.DocumentOptions);
        var canonical = RpcJson.Canonical(document.RootElement, compact: true);
        if (_baseline.TryGetValue(method, out var baseline)) Require(canonical == baseline, "Complete trace response differs across arms: " + method);
        else _baseline.Add(method, canonical);
    }

    internal static void ValidateLoad(string directory, long minimumRequests)
    {
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")))
            ?? throw new IOException("Missing trace load summary.");
        var metrics = summary["metrics"] as JsonObject ?? throw new IOException("Missing trace load metrics.");
        var requests = Metric(metrics["http_reqs"], "count");
        var rate = Metric(metrics["http_reqs"], "rate");
        Require(requests >= minimumRequests && requests == Math.Truncate(requests), "Trace load did not deliver enough requests.");
        Require(rate >= 2.85 && rate <= 3.15, "Trace load did not sustain the configured request rate.");
        Require(Metric(metrics["http_req_failed"], "rate", "value") == 0 && Metric(metrics["checks"], "fails") == 0 &&
            Metric(metrics["checks"], "passes") >= requests, "Trace load contains failed or missing HTTP/RPC checks.");
        Require(metrics["dropped_iterations"] is null || Metric(metrics["dropped_iterations"], "count") == 0, "Trace load dropped iterations.");

        var methodRequests = 0d;
        foreach (var method in new[] { "debug_traceBlockByNumber", "trace_block" })
        {
            var counts = metrics.Where(pair => IsMethodMetric(pair.Key, "http_reqs", method)).Select(pair => Metric(pair.Value, "count")).ToArray();
            Require(counts.Length > 0 && counts.All(count => count >= Math.Max(1, minimumRequests / 4) && count == counts[0]),
                "Trace load has incomplete method request counts: " + method);
            methodRequests += counts[0];
            var durations = metrics.Where(pair => IsMethodMetric(pair.Key, "http_req_duration", method)).Select(pair => pair.Value).ToArray();
            Require(durations.Length > 0, "Trace load has no method latency metrics: " + method);
            foreach (var duration in durations)
                foreach (var field in new[] { "avg", "med", "p(90)", "p(99)", "max" })
                    Require(Metric(duration, field) > 0, "Trace load has invalid method latency metrics: " + method);
        }
        Require(methodRequests == requests, "Method request counts do not cover the measured workload.");
    }

    private static double Metric(JsonNode? metric, params string[] fields)
    {
        Require(metric is JsonObject, "Required load metric is missing.");
        foreach (var field in fields)
        {
            var value = metric![field] ?? metric["values"]?[field];
            if (value is JsonValue scalar && scalar.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
        }
        throw new IOException("Required load metric value is missing or non-finite.");
    }

    private static bool IsMethodMetric(string key, string metric, string method)
    {
        if (!key.StartsWith(metric + "{", StringComparison.Ordinal) || !key.EndsWith('}')) return false;
        foreach (var tag in key[(metric.Length + 1)..^1].Split(','))
        {
            var parts = tag.Split(':', 2);
            if (parts.Length == 2 && parts[0].Trim() == "req_name" && parts[1].Trim().Trim('\'', '"') == method) return true;
        }
        return false;
    }

    private static long Number(JsonNode value) => value is JsonValue scalar && scalar.TryGetValue<long>(out var number)
        ? number : Convert.ToInt64(value.GetValue<string>()[2..], 16);

    private static int ComparePath(int[] left, int[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            if (left[index] != right[index]) return left[index].CompareTo(right[index]);
        return left.Length.CompareTo(right.Length);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }
}
