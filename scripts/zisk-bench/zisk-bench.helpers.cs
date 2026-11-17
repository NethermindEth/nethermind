// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class ZiskBench
{
    [GeneratedRegex(@"^STEPS\s+(\d[\d,]*)\s*$", RegexOptions.Multiline)]
    private static partial Regex StepsRegex();
    [GeneratedRegex(@"^MAIN\s+(\d[\d,]*)\s", RegexOptions.Multiline)]
    private static partial Regex MainRegex();
    [GeneratedRegex(@"^OPCODES\s+(\d[\d,]*)\s", RegexOptions.Multiline)]
    private static partial Regex OpcodesRegex();
    [GeneratedRegex(@"^PRECOMPILES\s+(\d[\d,]*)\s", RegexOptions.Multiline)]
    private static partial Regex PrecompilesRegex();
    [GeneratedRegex(@"^MEMORY\s+(\d[\d,]*)\s", RegexOptions.Multiline)]
    private static partial Regex MemoryRegex();
    [GeneratedRegex(@"^TOTAL\s+(\d[\d,]*)\s", RegexOptions.Multiline)]
    private static partial Regex TotalRegex();

    [GeneratedRegex(@"\A-?[0-9]+\z")]
    private static partial Regex IntegerValueRegex();
    [GeneratedRegex(@"\A[0-9]+\.ssz\z")]
    private static partial Regex InputNameRegex();

    internal const string Marker = "<!-- zisk-guest-benchmark-report -->";
    internal static readonly string[] Buckets = ["main", "opcodes", "precompiles", "memory"];

    internal static Dictionary<string, string>? Options(string[] args, string[] allowed, string[] required)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Options: " + string.Join(" ", allowed.Select(name => $"--{name} <value>")));
            return null;
        }
        var options = new Dictionary<string, string>();
        for (var index = 0; index < args.Length; index++)
        {
            var split = args[index].Split('=', 2);
            if (!split[0].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(split[0][2..])) throw new FormatException("unknown option: " + args[index]);
            var value = split.Length == 2 ? split[1] : ++index < args.Length ? args[index] : throw new FormatException("missing value: " + split[0]);
            options[split[0][2..]] = value;
        }
        foreach (var key in required) if (!options.ContainsKey(key)) throw new FormatException("required option: --" + key);
        return options;
    }

    internal static JsonObject Parse(string log, string input)
    {
        if (!InputNameRegex().IsMatch(input)) throw new ArgumentException("input must be a numeric block filename ending in .ssz");
        JsonNode Number(string name, bool steps = false)
        {
            var match = (name switch { "STEPS" => StepsRegex(), "MAIN" => MainRegex(), "OPCODES" => OpcodesRegex(), "PRECOMPILES" => PrecompilesRegex(), "MEMORY" => MemoryRegex(), "TOTAL" => TotalRegex(), _ => throw new ArgumentOutOfRangeException(nameof(name)) }).Match(log);
            if (!match.Success) throw new ArgumentException(steps ? "no STEPS line in the log — the run did not complete" : $"cost bucket '{name}' not found — did ziskemu run with -X?");
            return JsonNode.Parse(BigInteger.Parse(match.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))!;
        }
        var row = new JsonObject { ["input"] = input, ["steps"] = Number("STEPS", true) };
        foreach (var bucket in Buckets) row[bucket] = Number(bucket.ToUpperInvariant());
        row["total"] = Number("TOTAL");
        return row;
    }

    internal static (Dictionary<string, JsonObject> Rows, string Commit)? Load(string path)
    {
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
            var rows = document as JsonArray ?? (document as JsonObject)?["rows"] as JsonArray;
            var commitNode = (document as JsonObject)?["commit"];
            if (rows is null || (document is JsonObject wrapper && wrapper.ContainsKey("commit") && (commitNode is not JsonValue value || !value.TryGetValue<string>(out _)))) return null;
            var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var node in rows)
            {
                if (node is not JsonObject row || row["input"] is not JsonValue input || !input.TryGetValue<string>(out var name) ||
                    new[] { "steps", "total", "main", "opcodes", "precompiles", "memory" }.Any(field => row[field] is not JsonValue number || number.GetValueKind() != JsonValueKind.Number || !IntegerValueRegex().IsMatch(number.ToJsonString())) ||
                    !result.TryAdd(name, row)) return null;
            }
            return (result, commitNode?.GetValue<string>() ?? "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException or JsonException) { return null; }
    }

    internal static BigInteger Integer(JsonObject row, string field) => BigInteger.Parse(row[field]!.ToJsonString(), CultureInfo.InvariantCulture);
    internal static double Percent(BigInteger current, BigInteger before) => before.IsZero ? 0 : (double)(current - before) / (double)before * 100;
    internal static string Sign(double value) => (double.IsNegative(value) ? "-" : "+") + Math.Abs(value).ToString("F3", CultureInfo.InvariantCulture) + "%";
    internal static string Block(string name) => name.EndsWith(".ssz", StringComparison.Ordinal) ? name[..^4] : name;

    internal static (string Body, bool Regressed, bool Stale) Render(Dictionary<string, JsonObject> current, Dictionary<string, JsonObject>? baseline,
        string commit, string baselineCommit = "", string baseCommit = "", string commitKind = "commit")
    {
        var lines = new List<string> { Marker, "## Stateless guest cost", "" };
        var regressed = false;
        if (baseline is null || baseline.Count == 0) { baseline = null; baselineCommit = ""; }
        var stale = baseline is not null && baseCommit.Length > 0 && baselineCommit != baseCommit;
        string Short(string value) => value[..Math.Min(value.Length, 12)];
        string Number(BigInteger value) => value.ToString("N0", CultureInfo.InvariantCulture);
        if (stale)
        {
            lines.AddRange(["⚠️ **Baseline does not match this pull request's base; deltas are suppressed.**", "",
                $"Restored from `{(baselineCommit.Length == 0 ? "unrecorded" : Short(baselineCommit))}`, not this pull request's base (`{Short(baseCommit)}`).", ""]);
            baseline = null;
        }
        if (baseline is null)
        {
            lines.AddRange([stale ? "Only absolute measurements are shown." : "No baseline was restored, so this run only records where the guest stands. Pull requests compare against a baseline measured at their base commit.", "", "| block | steps | prover cost |", "|---|---:|---:|"]);
            foreach (var (name, row) in current.OrderBy(pair => pair.Key, StringComparer.Ordinal)) lines.Add($"| {Block(name)} | {Number(Integer(row, "steps"))} | {Number(Integer(row, "total"))} |");
        }
        else
        {
            lines.AddRange(["| block | steps | Δ steps | prover cost | Δ cost |", "|---|---:|---:|---:|---:|"]);
            foreach (var (name, row) in current.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!baseline.TryGetValue(name, out var before)) { lines.Add($"| {Block(name)} | {Number(Integer(row, "steps"))} | new | {Number(Integer(row, "total"))} | new |"); continue; }
                var stepDelta = Percent(Integer(row, "steps"), Integer(before, "steps"));
                var costDelta = Percent(Integer(row, "total"), Integer(before, "total"));
                regressed |= costDelta > 0.05;
                lines.Add($"| {Block(name)} | {Number(Integer(row, "steps"))} | {Sign(stepDelta)} | {Number(Integer(row, "total"))} | {Sign(costDelta)} |");
            }
            var shared = current.Keys.Where(baseline.ContainsKey).ToArray();
            BigInteger Sum(Dictionary<string, JsonObject> rows, string field) => shared.Aggregate(BigInteger.Zero, (sum, name) => sum + Integer(rows[name], field));
            if (shared.Length > 0)
            {
                lines.Add($"| **all {shared.Length} blocks** | **{Number(Sum(current, "steps"))}** | **{Sign(Percent(Sum(current, "steps"), Sum(baseline, "steps")))}** | **{Number(Sum(current, "total"))}** | **{Sign(Percent(Sum(current, "total"), Sum(baseline, "total")))}** |");
                lines.AddRange(["", "<details><summary>Variable cost by bucket, summed over shared blocks (excludes BASE)</summary>", "", "| bucket | before | after | Δ |", "|---|---:|---:|---:|"]);
                foreach (var bucket in Buckets) lines.Add($"| {bucket.ToUpperInvariant()} | {Number(Sum(baseline, bucket))} | {Number(Sum(current, bucket))} | {Sign(Percent(Sum(current, bucket), Sum(baseline, bucket)))} |");
                lines.AddRange(["", "</details>"]);
            }
            var missing = current.Keys.Except(baseline.Keys).Order(StringComparer.Ordinal).ToArray();
            var removed = baseline.Keys.Except(current.Keys).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0) lines.AddRange(["", $"Blocks absent from the baseline: {string.Join(", ", missing.Select(Block))}."]);
            if (removed.Length > 0)
            {
                regressed = true;
                lines.AddRange(["", $"Blocks the baseline measured but this run did not: {string.Join(", ", removed.Select(Block))}. They are in neither the table nor the totals."]);
            }
        }
        lines.AddRange(["", $"`{Short(commit)}` ({commitKind}) · {current.Count} pinned blocks · ziskemu is deterministic, so these figures are exact and any non-zero delta is real."]);
        if (baseline is not null) lines.AddRange(["", $"Compared against {(baselineCommit.Length == 0 ? "an unrecorded master commit" : "`" + Short(baselineCommit) + "`")}."]);
        if (regressed) lines.Insert(2, "⚠️ **Prover cost is up against the baseline or benchmark coverage decreased.**");
        return (string.Join('\n', lines) + "\n", regressed, stale);
    }
}
