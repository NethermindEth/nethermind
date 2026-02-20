// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Optimism.Test;

/// <summary>
/// Explicitly describes at which timestamp ranges a test case should be valid.
/// </summary>
// Not worth optimizing as it's expected to work with just a few intervals on average.
public class Valid
{
    // From - inclusive, To - exclusive
    // Null in From means negative infinity, in To - positive infinity
    internal readonly record struct Interval(ulong? From, ulong? To) : IComparable<Interval>
    {
        public static readonly Interval Empty = new(0, 0);
        public static readonly Interval Full = new(null, null);

        public bool Contains(ulong value)
        {
            if (From is null) return To is null || value < To;
            if (To is null) return value >= From;
            return value >= From && value < To;
        }

        // next must be same or larger in comparison
        public bool TryUnionWithNext(Interval next, out Interval combined)
        {
            combined = (To, next.From) switch
            {
                (null, _) => new(MinFrom(From, next.From), null),
                (_, null) => new(null, MaxTo(To, next.To)),
                var (to, from) when to >= from => new(MinFrom(From, next.From), MaxTo(To, next.To)),
                _ => Empty
            };

            return combined != Empty;
        }

        public int CompareTo(Interval other) => (From, other.From) switch
        {
            var (f1, f2) when f1 == f2 => CompareTo(To, other.To),
            (null, var f2) => -1,
            (var f1, null) => 1,
            (ulong f1, ulong f2) => f1.CompareTo(f2)
        };

        private static int CompareTo(ulong? to1, ulong? to2) => (to1, to2) switch
        {
            var (t1, t2) when t1 == t2 => 0,
            (null, var t2) => 1,
            (var t1, null) => -1,
            (ulong t1, ulong t2) => t1.CompareTo(t2)
        };

        private static ulong? MinFrom(ulong? a, ulong? b) =>
            !a.HasValue || !b.HasValue ? null : Math.Min(a.Value, b.Value);

        private static ulong? MaxTo(ulong? a, ulong? b) =>
            !a.HasValue || !b.HasValue ? null : Math.Max(a.Value, b.Value);

        public override string ToString() => (From, To) switch
        {
            (null, null) => "always",
            (0, 0) => "never",
            (ulong from, null) => $"since {ForkName(from)}",
            (null, ulong to) => $"before {ForkName(to)}",
            (ulong from, ulong to) => $"between {ForkName(from)} and {ForkName(to)}"
        };
    }

    private static string ForkName(ulong value) => Fork.At.TryGetValue(value, out Fork? fork) ? fork.Name : $"{value}";

    private readonly List<Interval> _intervals;

    private Valid(List<Interval> intervals) => _intervals = intervals;
    private Valid(Interval interval) : this([interval]) { }

    public static readonly Valid Always = new(Interval.Full);
    public static readonly Valid Never = new(Interval.Empty);

    public static Valid Since(ulong from) => new(new Interval(from, null));
    public static Valid Before(ulong to) => new(new Interval(null, to));
    public static Valid Between(ulong from, ulong to) => new(new Interval(from, to));

    public static Valid operator |(Valid v1, Valid v2)
    {
        IEnumerable<Interval> sorted = v1._intervals.Concat(v2._intervals).Order();
        List<Interval> merged = new(v1._intervals.Count + v2._intervals.Count);

        Interval current = Interval.Empty;
        foreach (Interval next in sorted)
        {
            if (current.TryUnionWithNext(next, out Interval combined))
            {
                current = combined;
            }
            else
            {
                if (current != Interval.Empty) merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return new Valid(merged);
    }

    public bool On(ulong timestamp) => _intervals.Any(i => i.Contains(timestamp));

    public override string ToString() => $"Valid: {string.Join("; ", _intervals)}";
}
