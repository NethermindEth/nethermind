// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace Nethermind.TestSelection.Collection;

/// <summary>
/// Reads test failures and project timings out of job names and job logs. A job runs either one test project
/// (<c>Run Nethermind.Evm.Test (ubuntu-latest)</c>), a pack of projects one after another, each in a log group
/// named after it, an EEST nethtest lane, or Hive.
/// </summary>
public static partial class JobLogs
{
    public const string Eest = "EEST";
    public const string Hive = "Hive";

    /// <summary>The unit a job runs: EEST, Hive, a test project, or a pack name; <c>null</c> for any other job.</summary>
    public static string? UnitOf(string jobName)
    {
        if (jobName.StartsWith("EEST ", StringComparison.Ordinal))
        {
            return Eest;
        }

        if (jobName.StartsWith("Hive ", StringComparison.Ordinal))
        {
            return Hive;
        }

        Match match = TestJob().Match(jobName);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// <c>type|setup</c> of a nethtest job. nethermind-tests.yml names it <c>EEST nethtest / nethtest &lt;type&gt;
    /// &lt;setup&gt; &lt;chunk&gt;</c>; run-nethtest.yml alone drops the prefix.
    /// </summary>
    public static string? NethtestLane(string jobName)
    {
        int prefix = jobName.IndexOf(" / ", StringComparison.Ordinal);
        string[] words = jobName[(prefix < 0 ? 0 : prefix + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3 && words[0] == "nethtest" ? $"{words[1]}|{words[2]}" : null;
    }

    /// <summary>
    /// The failed tests in a job's log: <c>EEST|lane|test|fork</c> for nethtest, <c>Project|test</c> otherwise. In a
    /// pack, a failure belongs to the project of the log group it appears in.
    /// </summary>
    public static IReadOnlySet<string> TestFailures(string log, string jobName)
    {
        HashSet<string> failures = [];
        string? unit = UnitOf(jobName);
        if (unit == Eest)
        {
            string? lane = NethtestLane(jobName);
            foreach (Match match in NethtestFailure().Matches(AnsiEscape().Replace(log, string.Empty)))
            {
                failures.Add($"{Eest}|{lane}|{match.Groups[1].Value}|{match.Groups[2].Value}");
            }

            return failures;
        }

        string? project = IsProject(unit) ? unit : null;
        foreach (string line in AnsiEscape().Replace(log, string.Empty).Split('\n'))
        {
            if (GroupStart().Match(line) is { Success: true } group && IsProject(group.Groups[1].Value))
            {
                project = group.Groups[1].Value;
            }
            else if (MtpFailure().Match(line) is { Success: true } failure)
            {
                failures.Add($"{project ?? unit}|{failure.Groups[1].Value}");
            }
        }

        return failures;
    }

    /// <summary>Seconds each project of a pack ran, from the timestamps of its log group.</summary>
    public static IReadOnlyDictionary<string, double> GroupSeconds(string log)
    {
        Dictionary<string, double> seconds = [];
        (string Project, DateTimeOffset Start)? open = null;
        foreach (string line in log.Split('\n'))
        {
            if (TimestampedGroup().Match(line) is not { Success: true } match)
            {
                continue;
            }

            DateTimeOffset at = DateTimeOffset.Parse(match.Groups[1].Value);
            if (open is { } current)
            {
                seconds[current.Project] = seconds.GetValueOrDefault(current.Project) + (at - current.Start).TotalSeconds;
                open = null;
            }

            if (match.Groups[2].Value == "group" && IsProject(match.Groups[3].Value))
            {
                open = (match.Groups[3].Value, at);
            }
        }

        return seconds;
    }

    public static bool IsProject(string? unit) => unit is not null && ProjectName().IsMatch(unit);

    // A project name, or a pack name, followed by the runner in parentheses or the lane in brackets.
    [GeneratedRegex(@"^Run ((?:Nethermind|Ethereum)\.[\w.]+|[^(\[]+?)(?: [(\[]|$)")]
    private static partial Regex TestJob();

    [GeneratedRegex(@"^(?:Nethermind|Ethereum)\.[\w.]+$")]
    private static partial Regex ProjectName();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"Z (?:FAIL|EXCEPTION) (?:\[\d+(?:/\d+)?\] )?(test_\w+)\[fork_(\w+?)[-\]]")]
    private static partial Regex NethtestFailure();

    [GeneratedRegex(@"Z\s+failed ([\w.]+)")]
    private static partial Regex MtpFailure();

    [GeneratedRegex(@"##\[group\]((?:Nethermind|Ethereum)\.[\w.]+)\s*$")]
    private static partial Regex GroupStart();

    [GeneratedRegex(@"^(\S+Z) ##\[(group|endgroup)\](\S*)")]
    private static partial Regex TimestampedGroup();
}
