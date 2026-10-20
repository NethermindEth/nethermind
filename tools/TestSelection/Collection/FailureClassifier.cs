// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Collection;

/// <summary>A failed job of a run.</summary>
public sealed record FailedJob(long Id, string Name);

/// <summary>
/// Turns the failed jobs of a run into test-level failures and infra failures. EEST jobs and the other jobs are judged
/// apart, so unit-test failures never hide EEST ones. When many jobs of a group fail at once, the first
/// <see cref="InfraSample"/> decide whether it was a runner or checkout problem, without reading every log.
/// </summary>
public static class FailureClassifier
{
    public const int InfraSample = 5;

    public static async Task<Failures> ClassifyAsync(IReadOnlyList<FailedJob> jobs, Func<FailedJob, Task<string>> readLog)
    {
        SortedSet<string> tests = new(StringComparer.Ordinal);
        SortedSet<string> infra = new(StringComparer.Ordinal);
        foreach (IGrouping<bool, FailedJob> group in jobs.GroupBy(static job => JobLogs.UnitOf(job.Name) == JobLogs.Eest))
        {
            Dictionary<long, IReadOnlySet<string>> found = await ReadGroupAsync([.. group], readLog);
            foreach (FailedJob job in group)
            {
                if (found.TryGetValue(job.Id, out IReadOnlySet<string>? failures) && failures.Count > 0)
                {
                    tests.UnionWith(failures);
                }
                else
                {
                    infra.Add(JobLogs.UnitOf(job.Name)!);
                }
            }
        }

        return new Failures([.. tests], [.. infra]);
    }

    /// <summary>
    /// Test-level failures per job. When the sample shows none, the rest of the group failed the same way and is not
    /// read.
    /// </summary>
    private static async Task<Dictionary<long, IReadOnlySet<string>>> ReadGroupAsync(IReadOnlyList<FailedJob> group, Func<FailedJob, Task<string>> readLog)
    {
        int sampleSize = group.Count <= InfraSample * 3 ? group.Count : InfraSample;
        Dictionary<long, IReadOnlySet<string>> found = [];
        await ReadAsync(group.Take(sampleSize), found, readLog);
        if (found.Values.Any(static failures => failures.Count > 0))
        {
            await ReadAsync(group.Skip(sampleSize), found, readLog);
        }

        return found;
    }

    private static async Task ReadAsync(IEnumerable<FailedJob> jobs, Dictionary<long, IReadOnlySet<string>> found, Func<FailedJob, Task<string>> readLog)
    {
        foreach (FailedJob job in jobs)
        {
            found[job.Id] = JobLogs.TestFailures(await readLog(job), job.Name);
        }
    }
}
