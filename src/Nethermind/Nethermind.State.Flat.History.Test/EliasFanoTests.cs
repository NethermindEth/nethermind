// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.State.Flat.History.Segmented;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class EliasFanoTests
{
    [Test]
    public void Empty_sequence_has_no_predecessor()
    {
        EliasFano.Reader reader = new(Encode(ReadOnlySpan<ulong>.Empty));
        Assert.That(reader.Count, Is.Zero);
        Assert.That(reader.Predecessor(100, out _, out _), Is.False);
    }

    [Test]
    public void Round_trips_values_by_rank()
    {
        ulong[] values = [5, 20, 30, 31, 1000, 1_000_000];
        EliasFano.Reader reader = new(Encode(values));

        Assert.That(reader.Count, Is.EqualTo(values.Length));
        for (int i = 0; i < values.Length; i++)
            Assert.That(reader[i], Is.EqualTo(values[i]), $"rank {i}");
    }

    [TestCaseSource(nameof(PredecessorSources))]
    public void Predecessor_matches_expected(ulong[] source)
    {
        EliasFano.Reader reader = new(Encode(source));
        for (int i = 0; i < source.Length; i++)
            Assert.That(reader[i], Is.EqualTo(source[i]), $"rank {i}");

        ulong max = source[^1];
        for (ulong q = 0; q <= max + 3; q += (max / 257) + 1)
        {
            bool found = reader.Predecessor(q, out int rank, out ulong value);
            (int eRank, ulong eValue) = BruteForceSearch(source, q);

            Assert.That(found, Is.EqualTo(eRank >= 0), $"found @ q={q}");

            if (eRank >= 0)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(rank, Is.EqualTo(eRank), $"rank @ q={q}");
                    Assert.That(value, Is.EqualTo(eValue), $"value @ q={q}");
                }
            }
        }
    }

    private static IEnumerable<TestCaseData> PredecessorSources()
    {
        yield return Source("single", [42]);
        yield return Source("sparse", [5, 20, 30]);
        yield return Source("dense", [0, 1, 2, 3]);
        yield return Source("wide-gaps", [100, 1_000_000, 5_000_000_000]);

        foreach ((int count, uint spread) in new[] { (1, 3u), (37, 5u), (500, 40u), (2000, 10_000_000u) })
            yield return Source($"random({count},{spread})", Generate(count, spread));

        static TestCaseData Source(string name, ulong[] values) => new TestCaseData(values).SetArgDisplayNames(name);

        static ulong[] Generate(int count, uint spread)
        {
            Random rng = new(count * 31 + (int)spread);
            SortedSet<ulong> set = [];
            while (set.Count < count) set.Add((ulong)rng.NextInt64(0, count * (long)spread + 1));
            return [.. set];
        }
    }

    private static (int rank, ulong value) BruteForceSearch(ulong[] values, ulong query)
    {
        for (int i = values.Length - 1; i >= 0; i--)
        {
            if (values[i] <= query)
                return (i, values[i]);
        }

        return (-1, 0);
    }

    private static byte[] Encode(ReadOnlySpan<ulong> values)
    {
        byte[] blob = new byte[EliasFano.GetEncodedLength(values)];
        int written = EliasFano.Encode(values, blob);
        Assert.That(written, Is.EqualTo(blob.Length));
        return blob;
    }
}
