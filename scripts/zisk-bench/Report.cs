#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include ZiskBench.Helpers.cs

try
{
    var options = ZiskBench.Options(args, ["current", "baseline", "commit", "commit-kind", "base-commit", "summary", "github-output"], ["current"]);
    if (options is null) return 0;
    var current = ZiskBench.Load(options["current"]) ?? throw new ArgumentException("invalid measurements to report");
    if (current.Rows.Count == 0) throw new ArgumentException("no measurements to report");
    var baseline = options.TryGetValue("baseline", out var path) ? ZiskBench.Load(path) : null;
    var (report, regressed, stale) = ZiskBench.Render(current.Rows, baseline?.Rows, options.GetValueOrDefault("commit", ""),
        baseline?.Commit ?? "", options.GetValueOrDefault("base-commit", ""), options.GetValueOrDefault("commit-kind", "commit"));
    Console.WriteLine(report);
    if (options.TryGetValue("summary", out path)) File.AppendAllText(path, report);
    if (options.TryGetValue("github-output", out path))
    {
        var delimiter = "ZISK_REPORT_" + Guid.NewGuid().ToString("N");
        while (report.Contains(delimiter, StringComparison.Ordinal)) delimiter = "ZISK_REPORT_" + Guid.NewGuid().ToString("N");
        File.AppendAllText(path, $"regressed={regressed.ToString().ToLowerInvariant()}\nstale={stale.ToString().ToLowerInvariant()}\nreport<<{delimiter}\n{report}\n{delimiter}\n");
    }
    return 0;
}
catch (FormatException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
