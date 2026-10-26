#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include Profiles.Helpers.cs

using System.Globalization;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
try
{
    if (args.Length < 2 || args[0] is not ("top" or "total" or "native" or "compare"))
        throw new ArgumentException("usage: PerfReport.cs top|total|native <profile.folded> [N], or compare <a.folded> <b.folded> [N]");
    var compare = args[0] == "compare";
    if (compare && args.Length < 3) throw new ArgumentException("compare requires two profiles");
    var limitIndex = compare ? 3 : 2;
    var limit = args.Length > limitIndex ? int.Parse(args[limitIndex], CultureInfo.InvariantCulture) : 30;
    var (counts, total) = Profiles.Aggregate(args[1], args[0] == "total");
    if (compare)
    {
        var (other, otherTotal) = Profiles.Aggregate(args[2], false);
        var rows = counts.Keys.Union(other.Keys, StringComparer.Ordinal).Select(frame =>
        {
            var a = Math.Round(counts.GetValueOrDefault(frame) * 100.0 / total, 4);
            var b = Math.Round(other.GetValueOrDefault(frame) * 100.0 / otherTotal, 4);
            return (Frame: frame, A: a, B: b, Delta: b - a);
        }).OrderBy(row => row.Delta).ThenBy(row => row.Frame, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"\n  Largest self-time shifts, {Path.GetFileName(args[1])} -> {Path.GetFileName(args[2])} (percentage points of profile)\n");
        Console.WriteLine($"  {"Frame",-64} {"A %",9} {"B %",9} {"Delta",9}");
        Console.WriteLine($"  {new string('-', 50),-64} {"-------",9} {"-------",9} {"-------",9}");
        var half = Math.Max(1, (limit + 1) / 2);
        var start = rows.Length - half > half ? rows.Length - half : half;
        for (var i = 0; i < rows.Length; i++)
        {
            if (i >= half && i < start)
            {
                if (i == half) Console.WriteLine($"  {"...",-64} {"",9} {"",9} {"",9}");
                continue;
            }
            var row = rows[i];
            Console.WriteLine($"  {Profiles.Display(row.Frame, 64),-64} {row.A,8:F2}% {row.B,8:F2}% {row.Delta.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture),8}");
        }
    }
    else
    {
        var title = args[0] switch { "total" => "total time", "native" => "native/unmanaged frames by self time", _ => "self time" };
        Console.WriteLine($"\n  Top {limit} {(args[0] == "native" ? "" : "by ")}{title} — {Path.GetFileName(args[1])}\n");
        Console.WriteLine($"  {"#",-4} {"Frame",-72} {"Samples",10} {(args[0] == "total" ? "Total %" : "Self %"),8}");
        Console.WriteLine($"  {"---",-4} {new string('-', 60),-72} {"--------",10} {"------",8}");
        var rank = 0;
        foreach (var (frame, count) in counts.Where(pair => args[0] != "native" || !Profiles.IsManaged(pair.Key)).OrderByDescending(pair => pair.Value).ThenByDescending(pair => pair.Key, StringComparer.Ordinal).Take(Math.Max(0, limit)))
            Console.WriteLine($"  {++rank,-4} {Profiles.Display(frame, 72),-72} {count,10} {count * 100.0 / total,7:F2}%");
    }
    Console.WriteLine();
    return 0;
}
catch (Exception exception) when (exception is IOException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
