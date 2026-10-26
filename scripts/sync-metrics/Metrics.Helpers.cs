// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class Metrics
{
    [GeneratedRegex(@"Accounts=(\d+), Slots=(\d+)")]
    private static partial Regex StateCountsRegex();
    [GeneratedRegex(@"(?:^|\t)(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.\d+)?Z\s")]
    private static partial Regex TimestampRegex();

    internal static readonly Dictionary<string, string> Markers = new()
    {
        ["snap_start"] = "Starting snap sync.", ["snap_end"] = "Snap sync completed.",
        ["state_finished"] = "STATE SYNC FINISHED", ["verify_start"] = "Collecting trie stats",
        ["drain_start"] = "Waiting for storage verification workers", ["verify_end_flat"] = "Verification complete.",
        ["verify_end_halfpath"] = "Stats after finishing state"
    };

    internal static void ParseLine(string line, Dictionary<string, DateTimeOffset> found, JsonObject counters)
    {
        var timestamp = TimestampRegex().Match(line);
        if (!timestamp.Success) return;
        foreach (var (key, marker) in Markers)
        {
            if (found.ContainsKey(key) || !line.Contains(marker, StringComparison.Ordinal)) continue;
            found[key] = DateTimeOffset.ParseExact(timestamp.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var count = StateCountsRegex().Match(line);
            if (key == "verify_end_flat" && count.Success)
            {
                counters["accounts"] = JsonNode.Parse(BigInteger.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                counters["slots"] = JsonNode.Parse(BigInteger.Parse(count.Groups[2].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    internal static double? Minutes(Dictionary<string, DateTimeOffset> found, string start, string end) =>
        found.TryGetValue(start, out var first) && found.TryGetValue(end, out var last) && last >= first
            ? RoundMinutes((last - first).TotalSeconds) : null;

    internal static double RoundMinutes(double seconds)
    {
        // Python rounds the exact binary value to decimal digits; Math.Round first scales the double,
        // which can turn values just above/below a midpoint into an exact tie (for example 0.05).
        var bits = BitConverter.DoubleToUInt64Bits(seconds / 60);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = new BigInteger(bits & 0x000fffffffffffff);
        if (exponent != 0) mantissa += BigInteger.One << 52;
        var shift = exponent == 0 ? -1074 : exponent - 1075;
        var numerator = mantissa * 10;
        var denominator = BigInteger.One;
        if (shift >= 0) numerator <<= shift;
        else denominator <<= -shift;
        var rounded = BigInteger.DivRem(numerator, denominator, out var remainder);
        var comparison = (remainder * 2).CompareTo(denominator);
        if (comparison > 0 || comparison == 0 && !rounded.IsEven) rounded++;
        return (double)rounded / 10;
    }

    internal static JsonObject Record(string network, string mode, string? image, Dictionary<string, DateTimeOffset> found, JsonObject counters)
    {
        var flat = mode.Equals("flat", StringComparison.OrdinalIgnoreCase);
        var end = flat ? "verify_end_flat" : "verify_end_halfpath";
        var missing = Markers.Keys.Where(key => key != (flat ? "verify_end_halfpath" : "verify_end_flat") && (flat || key != "drain_start") && !found.ContainsKey(key));
        if (missing.Any()) Console.Error.WriteLine($"Markers not found in log: {string.Join(", ", missing)}");
        var result = new JsonObject { ["network"] = network, ["mode"] = mode };
        if (!string.IsNullOrEmpty(image)) result["image"] = image;
        foreach (var (field, start, finish) in new[] { ("snap_min", "snap_start", "snap_end"), ("heal_min", "snap_end", "state_finished"), ("verify_min", "verify_start", end), ("drain_min", "drain_start", "verify_end_flat") })
            if (Minutes(found, start, finish) is { } minutes) result[field] = minutes;
        foreach (var (key, value) in counters) result[key] = value?.DeepClone();
        return result;
    }

    internal static async Task Publish(string directory, string history, string repository, long runId, string? attemptOverride, string? triggerSha, Func<string, CancellationToken, Task<JsonNode>> api, CancellationToken cancellationToken)
    {
        var run = await api($"repos/{repository}/actions/runs/{runId}", cancellationToken);
        var attempt = int.Parse(attemptOverride ?? run["run_attempt"]!.ToString(), CultureInfo.InvariantCulture);
        var durations = new Dictionary<string, double>();
        for (var page = 1; ; page++)
        {
            var jobs = (await api($"repos/{repository}/actions/runs/{runId}/attempts/{attempt}/jobs?per_page=100&page={page}", cancellationToken))["jobs"]!.AsArray();
            foreach (var job in jobs)
            {
                var name = job!["name"]!.GetValue<string>();
                if (name.EndsWith(" / sync", StringComparison.Ordinal)) name = name[..^7];
                else if (name.Contains(" / ", StringComparison.Ordinal)) continue;
                if (!name.StartsWith("Sync ", StringComparison.Ordinal) || job["conclusion"]?.ToString() != "success" || job["completed_at"] is null) continue;
                var elapsed = DateTimeOffset.Parse(job["completed_at"]!.ToString(), CultureInfo.InvariantCulture) - DateTimeOffset.Parse(job["started_at"]!.ToString(), CultureInfo.InvariantCulture);
                if (elapsed >= TimeSpan.Zero) durations[name] = RoundMinutes(elapsed.TotalSeconds);
            }
            if (jobs.Count < 100) break;
        }
        var sha = string.IsNullOrEmpty(triggerSha) ? run["head_sha"]!.ToString() : triggerSha;
        var records = new List<JsonObject>();
        foreach (var file in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var source = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken))!.AsObject();
            var record = new JsonObject { ["run_id"] = runId, ["run_attempt"] = attempt, ["date"] = run["created_at"]!.DeepClone(), ["event"] = run["event"]!.DeepClone(), ["commit"] = sha[..Math.Min(10, sha.Length)] };
            foreach (var (key, value) in source) record[key] = value?.DeepClone();
            record["source"] = "ci";
            var job = $"Sync {record["network"]} ({record["mode"]})";
            if (durations.TryGetValue(job, out var duration)) record["job_min"] = duration;
            else Console.WriteLine($"No successful job matched '{job}'; job_min omitted.");
            records.Add(record);
        }
        if (records.Count == 0) { Console.WriteLine("No metrics records found."); return; }
        static string Key(JsonNode node) => new JsonArray(node["run_id"]?.DeepClone(), node["network"]?.DeepClone(), node["mode"]?.DeepClone()).ToJsonString();
        var replaced = records.Select(Key).ToHashSet();
        var lines = new List<string>();
        if (File.Exists(history))
            foreach (var line in await File.ReadAllLinesAsync(history, cancellationToken))
                if (line.Trim().Length > 0 && !replaced.Contains(Key(JsonNode.Parse(line)!))) lines.Add(line.Trim());
        foreach (var record in records)
        {
            lines.Add(record.ToJsonString());
            Console.WriteLine($"Appending: {lines[^1]}");
        }
        await File.WriteAllTextAsync(history, string.Join('\n', lines) + "\n", cancellationToken);
    }
}
