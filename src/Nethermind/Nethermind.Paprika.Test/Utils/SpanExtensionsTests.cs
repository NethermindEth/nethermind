// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Utils;

namespace Paprika.Tests.Utils;

public class SpanExtensionsTests
{
    [Test]
    public void Batch_empty()
    {
        SpanExtensions.RangeEnumerator<int> enumerator = Span<int>.Empty.BatchConsecutive();

        End(enumerator);
    }

    [Test]
    public void Batch_single_consecutive()
    {
        const int start0 = 1;
        Span<int> span = [start0, 2, 3];

        SpanExtensions.RangeEnumerator<int> enumerator = span.BatchConsecutive();

        MoveNext(ref enumerator, new(start0, span.Length));

        End(enumerator);
    }

    [Test]
    public void Batch_single_consecutive_with_max()
    {
        const int start0 = 1;
        const int start1 = 3;
        Span<int> span = [start0, 2, start1, 4];

        const int max = 2;

        SpanExtensions.RangeEnumerator<int> enumerator = span.BatchConsecutive(max);

        MoveNext(ref enumerator, new(start0, max));
        MoveNext(ref enumerator, new(start1, max));

        End(enumerator);
    }

    [Test]
    public void Batch_splits()
    {
        const int start0 = 1;
        const int start1 = 3;
        Span<int> span = [start0, start1, 4, 5, 6];

        const int at = 1;

        SpanExtensions.RangeEnumerator<int> enumerator = span.BatchConsecutive();

        MoveNext(ref enumerator, new(start0, at));
        MoveNext(ref enumerator, new(start1, span.Length - at));

        End(enumerator);
    }

    [Test]
    public void Batch_splits_with_max()
    {
        const int start0 = 1;
        const int start1 = 3;
        const int start2 = 5;
        const int start3 = 7;

        Span<int> span = [start0, start1, 4, start2, 6, start3];

        const int max = 2;

        SpanExtensions.RangeEnumerator<int> enumerator = span.BatchConsecutive(max);

        MoveNext(ref enumerator, new(start0, 1));
        MoveNext(ref enumerator, new(start1, max));
        MoveNext(ref enumerator, new(start2, max));
        MoveNext(ref enumerator, new(start3, 1));

        End(enumerator);
    }

    private static void MoveNext(ref SpanExtensions.RangeEnumerator<int> enumerator, (int Start, int Length) range)
    {
        if (!enumerator.MoveNext())
            Assert.Fail($"Expected range {range}, but the enumeration ended");

        Assert.That(enumerator.Current, Is.EqualTo(range));
    }

    private static void End(SpanExtensions.RangeEnumerator<int> enumerator) => Assert.That(enumerator.MoveNext(), Is.False);
}
