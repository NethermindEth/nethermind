// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TestSelection.Journal;

/// <summary>
/// Keeps only the failures that the changes caused. A test that also fails on master, or on
/// <see cref="OtherBranches"/> other branches, within <see cref="Window"/> failed for its environment: a flaky test,
/// an outage or a broken base. The model must not learn from those, and the replay must not count them as misses.
/// </summary>
public static class EnvironmentFilter
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public const int OtherBranches = 2;

    public static IReadOnlyList<JournalEntry> WithoutEnvironment(IReadOnlyList<JournalEntry> journal)
    {
        Dictionary<string, FailureTimeline> timelines = Timelines(journal);
        List<JournalEntry> filtered = new(journal.Count);
        foreach (JournalEntry entry in journal)
        {
            filtered.Add(entry.Failed.Tests.Count == 0 ? entry : entry with
            {
                Failed = entry.Failed with { Tests = [.. entry.Failed.Tests.Where(test => timelines[test].IsCausedBy(entry))] },
            });
        }

        return filtered;
    }

    private static Dictionary<string, FailureTimeline> Timelines(IReadOnlyList<JournalEntry> journal)
    {
        Dictionary<string, List<(DateTimeOffset At, string Branch)>> failures = [];
        foreach (JournalEntry entry in journal)
        {
            foreach (string test in entry.Failed.Tests)
            {
                if (!failures.TryGetValue(test, out List<(DateTimeOffset At, string Branch)>? events))
                {
                    failures[test] = events = [];
                }

                events.Add((entry.At, entry.Branch));
            }
        }

        return failures.ToDictionary(static pair => pair.Key, static pair => new FailureTimeline(pair.Value));
    }

    /// <summary>When and on which branches one test failed, sorted by time for a window lookup.</summary>
    private sealed class FailureTimeline
    {
        private readonly DateTimeOffset[] _times;
        private readonly string[] _branches;

        public FailureTimeline(List<(DateTimeOffset At, string Branch)> events)
        {
            events.Sort(static (left, right) => left.At.CompareTo(right.At));
            _times = [.. events.Select(static e => e.At)];
            _branches = [.. events.Select(static e => e.Branch)];
        }

        public bool IsCausedBy(JournalEntry entry)
        {
            int first = LowerBound(entry.At - Window);
            int end = UpperBound(entry.At + Window);
            HashSet<string> others = [];
            for (int i = first; i < end; i++)
            {
                if (_branches[i] != entry.Branch)
                {
                    others.Add(_branches[i]);
                }
            }

            return !others.Contains(JournalEntry.Master) && others.Count < OtherBranches;
        }

        private int LowerBound(DateTimeOffset value)
        {
            int index = Array.BinarySearch(_times, value);
            if (index < 0)
            {
                return ~index;
            }

            while (index > 0 && _times[index - 1] == value)
            {
                index--;
            }

            return index;
        }

        private int UpperBound(DateTimeOffset value)
        {
            int index = Array.BinarySearch(_times, value);
            if (index < 0)
            {
                return ~index;
            }

            while (index < _times.Length && _times[index] == value)
            {
                index++;
            }

            return index;
        }
    }
}
