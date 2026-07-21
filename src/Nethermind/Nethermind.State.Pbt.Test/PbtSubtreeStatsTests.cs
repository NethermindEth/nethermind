// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSubtreeStatsTests
{
    private const long MaxStemCount = (1L << 48) - 1;

    [TestCase(0L)]
    [TestCase(1L)]
    [TestCase(0x0102030405L)]
    [TestCase(MaxStemCount)]
    public void WriteReadRoundTrip(long stemCount)
    {
        byte[] encoded = new byte[PbtSubtreeStats.EncodedLength];
        new PbtSubtreeStats(stemCount).Write(encoded);

        Assert.That(PbtSubtreeStats.Read(encoded).StemCount, Is.EqualTo(stemCount));
    }

    [TestCase(7L, 3L)]
    [TestCase(1L, 1L)]
    [TestCase(0L, 0L)]
    [TestCase(2L, 5L)]
    public void DeltasComposeBackToTheValueTheyMeasure(long before, long after)
    {
        PbtSubtreeStats beforeStats = new(before);
        PbtSubtreeStats afterStats = new(after);
        PbtSubtreeStats delta = afterStats - beforeStats;

        Assert.That(delta.StemCount, Is.EqualTo(after - before));
        Assert.That(beforeStats + delta, Is.EqualTo(afterStats));
        Assert.That(delta.IsZero, Is.EqualTo(before == after));
    }

    [Test]
    public void OneStemIsTheDeltaOfAStemAppearing()
    {
        Assert.That(PbtSubtreeStats.OneStem.StemCount, Is.EqualTo(1));
        Assert.That((default(PbtSubtreeStats) - PbtSubtreeStats.OneStem).StemCount, Is.EqualTo(-1), "and its disappearing");
        Assert.That(default(PbtSubtreeStats).IsZero);
    }
}
