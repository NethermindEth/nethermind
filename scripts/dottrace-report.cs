#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
try
{
    if (args.Length < 2 || args[0] is not ("top" or "compare"))
        throw new ArgumentException("usage: dottrace-report.cs top <report.xml> [N], or compare <a.xml> <b.xml> [N]");
    var compare = args[0] == "compare";
    if (compare && args.Length < 3) throw new ArgumentException("compare requires two reports");
    var limitIndex = compare ? 3 : 2;
    var limit = args.Length > limitIndex ? int.Parse(args[limitIndex], CultureInfo.InvariantCulture) : 30;
    var a = Extract(args[1]);
    if (!compare)
    {
        Console.WriteLine($"\n  Top {limit} by OwnTime — {Path.GetFileNameWithoutExtension(args[1])}\n");
        Console.WriteLine($"  {"#",-4}  {"Function",-65}  {"OwnTime",12}  {"TotalTime",12}");
        Console.WriteLine($"  {"---",-4}  {new string('-', 65),-65}  {"----------",12}  {"----------",12}");
        var rank = 0;
        foreach (var (name, times) in a.OrderByDescending(pair => pair.Value.Own).ThenByDescending(pair => pair.Key, StringComparer.Ordinal).Take(Math.Max(0, limit)))
            Console.WriteLine($"  {++rank,-4}  {name,-65}  {times.Own,12:F0}  {times.Total,12:F0}");
        Console.WriteLine();
    }
    else
    {
        var b = Extract(args[2]);
        var rows = a.Keys.Intersect(b.Keys, StringComparer.Ordinal).Select(name =>
        {
            var delta = b[name].Own - a[name].Own;
            return (Name: name, A: a[name].Own, B: b[name].Own, Delta: delta, Percent: a[name].Own > 0 ? delta / a[name].Own * 100 : 999999);
        }).ToArray();
        Console.WriteLine($"\n  Comparison: [A] {Name(args[1])}\n          vs  [B] {Name(args[2])}");
        foreach (var regressions in new[] { true, false })
        {
            Console.WriteLine($"\n  {(regressions ? "REGRESSIONS (B slower than A)" : "IMPROVEMENTS (B faster than A)")} — top {limit} by absolute delta\n");
            Console.WriteLine($"  {"Function",-60}  {"[A] Own",10}  {"[B] Own",10}  {"Delta",10}  {"Change",8}");
            Console.WriteLine($"  {new string('-', 60),-60}  {"--------",10}  {"--------",10}  {"--------",10}  {"------",8}");
            var sorted = regressions ? rows.OrderByDescending(row => row.Delta).ThenByDescending(row => row.Name, StringComparer.Ordinal) : rows.OrderBy(row => row.Delta).ThenBy(row => row.Name, StringComparer.Ordinal);
            foreach (var row in sorted.Take(Math.Max(0, limit)))
                Console.WriteLine($"  {row.Name,-60}  {row.A,10:F0}  {row.B,10:F0}  {row.Delta,10:F0}  {row.Percent.ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture),7}%");
        }
        Console.WriteLine();
    }
    return 0;
}
catch (Exception exception) when (exception is IOException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

static string Name(string path) => Path.GetFileNameWithoutExtension(path).Replace("nethermind-multi-", "", StringComparison.Ordinal);

static Dictionary<string, (double Total, double Own)> Extract(string path)
{
    // Keep the reporter's escaped FQN text, which is also the comparison key in existing reports.
    var values = new Dictionary<string, (double Total, double Own)>(StringComparer.Ordinal);
    foreach (var line in File.ReadLines(path))
    {
        if (!line.Contains("<Function ", StringComparison.Ordinal)) continue;
        var match = FunctionRegex().Match(line);
        if (!match.Success) continue;
        var total = double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var totalTime) ? totalTime : 0;
        var own = double.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var ownTime) ? ownTime : 0;
        var name = match.Groups[1].Value;
        if (!values.TryGetValue(name, out var previous) || own > previous.Own) values[name] = (total, own);
    }
    return values;
}

partial class Program
{
    [GeneratedRegex(".*FQN=\"([^\"]*)\".*TotalTime=\"([^\"]*)\".*OwnTime=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionRegex();
}
