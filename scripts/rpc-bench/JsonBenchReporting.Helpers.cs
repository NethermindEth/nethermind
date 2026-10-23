// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

internal static class JsonBenchReporting
{
    internal static (string Markdown, double FailPercent) Performance(JsonObject root)
    {
        var metrics = root["metrics"] as JsonObject ?? (root["metrics"] is null ? [] : throw new JsonException("metrics must be an object"));
        var duration = metrics["http_req_duration"]; var requests = metrics["http_reqs"]; var failures = metrics["http_req_failed"]; var checks = metrics["checks"];
        var rate = Number(failures, "rate"); if (rate == 0) rate = Number(failures, "value");
        var passed = Number(checks, "passes"); var failed = Number(checks, "fails");
        var text = new StringBuilder("### Overall\n\n| metric | value |\n|---|---:|\n");
        text.Append(CultureInfo.InvariantCulture, $"| requests | {(long)Number(requests, "count")} |\n| throughput (req/s) | {Number(requests, "rate"):F2} |\n| http fail rate | {rate * 100:F2}% |\n");
        if (passed + failed > 0) text.Append(CultureInfo.InvariantCulture, $"| checks passed | {passed / (passed + failed) * 100:F2}% |\n");
        var fields = new[] { ("avg", "avg"), ("p50", "med"), ("p90", "p(90)"), ("p95", "p(95)"), ("p99", "p(99)"), ("max", "max") };
        foreach (var (label, key) in fields) text.Append(CultureInfo.InvariantCulture, $"| latency {label} (ms) | {Number(duration, key):F2} |\n");
        var methods = new List<(string Name, JsonNode? Values)>();
        foreach (var (key, value) in metrics)
        {
            if (!key.StartsWith("http_req_duration{", StringComparison.Ordinal)) continue;
            var match = Regex.Match(key, @"req_name:([^,}]+)");
            if (match.Success) methods.Add((match.Groups[1].Value.Trim().Trim('\'', '"'), value));
        }
        if (metrics["classes"] is JsonObject classes) methods.AddRange(classes.Select(pair => (pair.Key, pair.Value)));
        if (methods.Count > 0)
        {
            text.Append("\n### Per method (http_req_duration, ms)\n\n| method | avg | p50 | p90 | p95 | p99 | max |\n|---|---:|---:|---:|---:|---:|---:|\n");
            foreach (var (name, values) in methods.OrderBy(row => row.Name, StringComparer.Ordinal)) text.Append("| ").Append(name).Append(" | ").AppendJoin(" | ", fields.Select(field => Number(values, field.Item2).ToString("F2", CultureInfo.InvariantCulture))).Append(" |\n");
        }
        return (text.ToString(), rate * 100);
    }

    internal static double Number(JsonNode? node, string field)
    {
        if (node is not JsonObject obj) return 0;
        if (obj[field] is JsonValue value && value.TryGetValue<double>(out var number)) return number;
        return obj["values"] is JsonObject values && values[field] is JsonValue nested && nested.TryGetValue<double>(out number) ? number : 0;
    }

    internal static (string Markdown, int Differences) Comparison(JsonNode? root)
    {
        if (root is not (JsonArray or JsonObject)) throw new JsonException("comparison results must be an object or array");
        var rows = root as JsonArray ?? (root as JsonObject)?["results"] as JsonArray ?? (root as JsonObject)?["comparisons"] as JsonArray ?? [];
        var different = rows.OfType<JsonObject>().Where(row => row["differences"] is JsonObject { Count: > 0 } or JsonArray { Count: > 0 }).ToArray();
        var text = new StringBuilder($"**{rows.Count} calls compared, {different.Length} with response differences.**\n\n");
        if (different.Length > 0)
        {
            text.Append("| method | params | differences |\n|---|---|---|\n");
            foreach (var row in different.Take(50))
            {
                var parameters = (row["params"] ?? new JsonArray()).ToJsonString(); parameters = parameters[..Math.Min(80, parameters.Length)];
                var keys = row["differences"] is JsonObject fields ? string.Join(", ", fields.Select(pair => pair.Key).Order(StringComparer.Ordinal)) : string.Join(", ", Enumerable.Range(0, row["differences"]!.AsArray().Count));
                text.Append($"| {row["method"]} | {parameters} | {keys} |\n");
            }
            text.Append("\nFull diff detail: `comparison-results.json` / `comparison-report.html` in the artifact.\n");
        }
        return (text.ToString(), different.Length);
    }

    internal static async Task CaptureAsync(YamlMappingNode config, string source, string url, string output, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        await using var writer = File.CreateText(output);
        var sequence = 0;
        if (config.Children.TryGetValue(new YamlScalarNode("calls"), out var calls) && calls is YamlSequenceNode entries)
            foreach (var call in entries.Children.Cast<YamlMappingNode>())
            {
                if (call.Children.TryGetValue(new YamlScalarNode("file"), out var file))
                {
                    foreach (var line in File.ReadLines(Path.Combine(source, file.ToString().TrimStart('.', '/'))))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var request = JsonNode.Parse(line)!;
                        await Capture(request["method"]?.GetValue<string>(), request["params"]?.DeepClone() ?? new JsonArray());
                    }
                }
                else await Capture(call.Children.TryGetValue(new YamlScalarNode("method"), out var method) ? method.ToString() : null, call.Children.TryGetValue(new YamlScalarNode("params"), out var parameters) ? ToJson(parameters) : new JsonArray());
            }
        BenchmarkProcess.Log($"deep-check: captured {sequence} responses -> {output}");

        async Task Capture(string? method, JsonNode? parameters)
        {
            var identity = new JsonArray(JsonValue.Create(method), parameters?.DeepClone());
            using var identityDocument = JsonDocument.Parse(identity.ToJsonString());
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RpcJson.Canonical(identityDocument.RootElement))))[..16];
            JsonNode? response;
            try
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters?.DeepClone() };
                using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
                using var result = await http.PostAsync(url, content, cancellationToken); result.EnsureSuccessStatusCode();
                response = JsonNode.Parse(await result.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { response = new JsonObject { ["_capture_error"] = exception.Message }; }
            var row = new JsonObject { ["seq"] = sequence++, ["fp"] = fingerprint, ["method"] = method, ["response"] = response };
            await writer.WriteLineAsync(row.ToJsonString().AsMemory(), cancellationToken);
        }
    }

    internal static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair => KeyValuePair.Create(pair.Key.ToString(), ToJson(pair.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar when scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted => JsonValue.Create(scalar.Value),
        YamlScalarNode scalar when scalar.Value is "null" or "Null" or "NULL" or "~" or "" or null => null,
        YamlScalarNode scalar when scalar.Value is "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON" => JsonValue.Create(true),
        YamlScalarNode scalar when scalar.Value is "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF" => JsonValue.Create(false),
        YamlScalarNode scalar when long.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
        YamlScalarNode scalar when Regex.IsMatch(scalar.Value ?? "", @"^[+-]?0x[0-9a-fA-F]+$") => JsonValue.Create((scalar.Value![0] == '-' ? -1 : 1) * Convert.ToInt64(scalar.Value.TrimStart('+', '-')[2..], 16)),
        YamlScalarNode scalar when Regex.IsMatch(scalar.Value ?? "", @"^[+-]?(?:[0-9][0-9_]*\.[0-9_]*|\.[0-9_]+)(?:[eE][+-][0-9]+)?$") && double.TryParse(scalar.Value!.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => JsonValue.Create(number),
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => throw new IOException("Unsupported benchmark YAML node")
    };
}
