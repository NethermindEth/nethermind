#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Merge the per-shard guest cost rows into one results file and a Markdown report.
//
//   summarize.cs --results <dir> --release <tag> --commit <sha> --shards <json> --into <file> --summary <file>
//
// Each row is one benchmark block: its gas value, test id, and the ziskemu step count and cost
// buckets that scripts/zisk-bench/parse-stats.py extracts. The report lists, per gas value, the
// costliest blocks and the costliest block of every test module, which is where a regression or a
// new worst case shows first. `--shards` maps each gas value to its shard count, e.g. {"30M": 8}.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const int Top = 25;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

Dictionary<string, string> options = [];
for (int i = 0; i < args.Length; i += 2)
{
    if (!args[i].StartsWith("--") || i + 1 >= args.Length)
        return Usage();
    options[args[i][2..]] = args[i + 1];
}

foreach (string name in (string[])["results", "release", "commit", "shards", "into", "summary"])
{
    if (!options.ContainsKey(name))
        return Usage();
}

Dictionary<string, int> shardCounts = [];
using (JsonDocument shards = JsonDocument.Parse(options["shards"]))
{
    foreach (JsonProperty gas in shards.RootElement.EnumerateObject())
        shardCounts[gas.Name] = gas.Value.GetInt32();
}

// Shards write `<gas>-<shard>.json` only once every block of theirs ran and matched, so a missing
// file is a shard that failed or never ran.
string[] files = Directory.GetFiles(options["results"], "*.json", SearchOption.AllDirectories);
Array.Sort(files, StringComparer.Ordinal);
List<Row> rows = [];
foreach (string file in files)
{
    using FileStream stream = File.OpenRead(file);
    using JsonDocument shard = JsonDocument.Parse(stream);
    foreach (JsonElement row in shard.RootElement.EnumerateArray())
    {
        rows.Add(new Row(
            row.GetProperty("gas").GetString()!,
            row.GetProperty("id").GetString()!,
            row.GetProperty("steps").GetInt64(),
            row.GetProperty("main").GetInt64(),
            row.GetProperty("opcodes").GetInt64(),
            row.GetProperty("precompiles").GetInt64(),
            row.GetProperty("memory").GetInt64(),
            row.GetProperty("total").GetInt64()));
    }
}

rows.Sort(static (a, b) => GasKey(a.Gas) != GasKey(b.Gas) ? GasKey(a.Gas).CompareTo(GasKey(b.Gas)) : string.CompareOrdinal(a.Id, b.Id));
WriteResults(options["into"], rows);

List<string> gasValues = [.. shardCounts.Keys.OrderBy(GasKey)];
StringBuilder report = new();
report.AppendLine("## Stateless benchmark fixtures")
    .AppendLine()
    .AppendLine($"ZisK guest cost per benchmark block of `{options["release"]}`, built from `{options["commit"]}`.")
    .AppendLine()
    .AppendLine("| Gas value | Shards | Blocks | Max steps | Total steps |")
    .AppendLine("| --- | ---: | ---: | ---: | ---: |");

bool incomplete = false;
foreach (string gas in gasValues)
{
    List<Row> measured = rows.FindAll(row => row.Gas == gas);
    int done = files.Count(file => Path.GetFileName(file).StartsWith($"{gas}-", StringComparison.Ordinal));
    int expected = shardCounts[gas];
    incomplete |= done != expected;
    string shardCell = done == expected ? $"{done}" : $"{done} of {expected}";
    long max = measured.Count == 0 ? 0 : measured.Max(static row => row.Steps);
    report.AppendLine($"| {gas} | {shardCell} | {measured.Count} | {max:N0} | {measured.Sum(static row => row.Steps):N0} |");
}

foreach (string gas in gasValues)
{
    List<Row> costliest = [.. rows.Where(row => row.Gas == gas).OrderByDescending(static row => row.Steps)];
    if (costliest.Count == 0)
        continue;

    Dictionary<string, Row> worstPerModule = [];
    foreach (Row row in costliest)
        worstPerModule.TryAdd(Module(row.Id), row);

    report.AppendLine().AppendLine($"### {gas}").AppendLine()
        .AppendLine("<details><summary>Costliest blocks</summary>").AppendLine();
    AppendTable(report, "Case", costliest.Take(Top), static row => Case(row.Id));
    report.AppendLine().AppendLine("</details>").AppendLine()
        .AppendLine("<details><summary>Costliest block per test module</summary>").AppendLine();
    AppendTable(report, "Module", worstPerModule.Values.OrderByDescending(static row => row.Steps), static row => Module(row.Id));
    report.AppendLine().AppendLine("</details>");
}

File.AppendAllText(options["summary"], report.ToString());

if (incomplete)
{
    Console.Error.WriteLine("Some shards produced no results; see the report.");
    return 1;
}

return 0;

static int Usage()
{
    Console.Error.WriteLine("usage: summarize.cs --results <dir> --release <tag> --commit <sha> --shards <json> --into <file> --summary <file>");
    return 2;
}

static int GasKey(string gas) => int.Parse(gas.AsSpan(0, gas.Length - 1));

static string Case(string id) => id[(id.IndexOf("::", StringComparison.Ordinal) + 2)..];

// `tests/benchmark/compute/precompile/test_modexp.py::test_modexp[...]` -> `precompile/test_modexp`
static string Module(string id)
{
    Match match = ModuleRegex.Pattern.Match(id);
    return match.Success ? match.Groups[1].Value : id[..id.IndexOf("::", StringComparison.Ordinal)];
}

static void AppendTable(StringBuilder report, string header, IEnumerable<Row> rows, Func<Row, string> label)
{
    report.AppendLine($"| {header} | Steps | Cost |").AppendLine("| --- | ---: | ---: |");
    foreach (Row row in rows)
        report.AppendLine($"| `{label(row)}` | {row.Steps:N0} | {row.Total:N0} |");
}

static void WriteResults(string path, List<Row> rows)
{
    using FileStream stream = File.Create(path);
    using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
    writer.WriteStartArray();
    foreach (Row row in rows)
    {
        writer.WriteStartObject();
        writer.WriteString("gas", row.Gas);
        writer.WriteString("id", row.Id);
        writer.WriteNumber("steps", row.Steps);
        writer.WriteNumber("main", row.Main);
        writer.WriteNumber("opcodes", row.Opcodes);
        writer.WriteNumber("precompiles", row.Precompiles);
        writer.WriteNumber("memory", row.Memory);
        writer.WriteNumber("total", row.Total);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
}

record Row(string Gas, string Id, long Steps, long Main, long Opcodes, long Precompiles, long Memory, long Total);

static partial class ModuleRegex
{
    [GeneratedRegex(@"^tests/benchmark/compute/(.+?)\.py::")]
    public static partial Regex Pattern { get; }
}
