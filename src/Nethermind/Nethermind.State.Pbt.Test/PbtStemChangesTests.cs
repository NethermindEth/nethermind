// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtStemChangesTests
{
    // Distinct sub-index count → the variant that count must have promoted to.
    private static object[] TierCases =>
    [
        new object[] { 1, typeof(SingleStemChanges) },
        new object[] { 2, typeof(Length4StemChanges) },
        new object[] { 4, typeof(Length4StemChanges) },
        new object[] { 5, typeof(Length8StemChanges) },
        new object[] { 8, typeof(Length8StemChanges) },
        new object[] { 9, typeof(Length16StemChanges) },
        new object[] { 16, typeof(Length16StemChanges) },
        new object[] { 17, typeof(SortedStemChanges) },
        new object[] { 70, typeof(SortedStemChanges) },
    ];

    /// <summary>
    /// Across every variant: entries are added out of order, one is updated in place and one is
    /// cleared to zero; the map must promote to the expected variant, keep <see cref="IPbtStemChanges.Count"/>
    /// stable through updates/clears, and write entries strictly ascending with the latest (and retained
    /// zero) values.
    /// </summary>
    [TestCaseSource(nameof(TierCases))]
    public void PromotesAndWritesSorted(int distinctKeys, Type expectedVariant)
    {
        IPbtStemChanges map = PbtStemChanges.Rent();

        // insert descending so ordering is only correct if the map sorts
        for (int k = distinctKeys - 1; k >= 0; k--) map = map.Set((byte)k, Value(k + 1));

        ValueHash256 updated = Value(1000);
        map = map.Set(0, updated);                          // add-or-update, in place
        if (distinctKeys >= 2) map = map.Set(1, default);   // clear is retained, not dropped

        Assert.That(map, Is.InstanceOf(expectedVariant));
        Assert.That(map.Count, Is.EqualTo(distinctKeys));

        for (int i = 0; i < distinctKeys; i++)
        {
            Assert.That(map.SubIndexAt(i), Is.EqualTo((byte)i), "strictly ascending by sub-index");
            ValueHash256 expected = i == 0 ? updated
                : i == 1 ? default
                : Value(i + 1);
            Assert.That(map.Get(i), Is.EqualTo(expected));
        }

        PbtStemChanges.Return(map);
        Assert.That(PbtStemChanges.Rent().Count, Is.Zero, "a freshly rented map is empty");
    }

    /// <summary>
    /// Fuzzes the SIMD insertion search (<see cref="FixedStemChanges{TSearch}"/>), the binary-search insert
    /// (<see cref="SortedStemChanges"/>) and the promotion path by writing a random permutation of
    /// sub-indices (with a second pass of in-place overwrites) and checking the map reproduces a
    /// <see cref="SortedDictionary{TKey,TValue}"/> reference exactly, in order.
    /// </summary>
    [TestCase(1, 1)]
    [TestCase(4, 2)]
    [TestCase(8, 3)]
    [TestCase(9, 4)]
    [TestCase(16, 5)]
    [TestCase(17, 6)]
    [TestCase(64, 7)]
    [TestCase(256, 8)]
    public void MatchesSortedReferenceUnderRandomInsertion(int distinctKeys, int seed)
    {
        Random rng = new(seed);

        byte[] order = new byte[256];
        for (int i = 0; i < 256; i++) order[i] = (byte)i;
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        SortedDictionary<byte, ValueHash256> reference = [];
        IPbtStemChanges map = PbtStemChanges.Rent();

        // first pass writes every key; second pass overwrites the even keys in place with new values
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < distinctKeys; i++)
            {
                byte key = order[i];
                if (round == 1 && (key & 1) != 0) continue;
                ValueHash256 value = Value(key + round * 1000 + 1);
                reference[key] = value;
                map = map.Set(key, value);
            }
        }

        Assert.That(map.Count, Is.EqualTo(reference.Count));
        int index = 0;
        foreach ((byte subIndex, ValueHash256 value) in reference)
        {
            Assert.That(map.SubIndexAt(index), Is.EqualTo(subIndex));
            Assert.That(map.Get(index), Is.EqualTo(value));
            index++;
        }

        PbtStemChanges.Return(map);
    }

    // Sub-indices already in the map → the run's start and length. Each case is named for what it puts
    // the run's block copies up against.
    private static object[] RunCases =>
    [
        new object[] { Array.Empty<byte>(), (byte)0, 1, "empty map, lone leaf" },
        new object[] { Array.Empty<byte>(), (byte)0, 2, "empty map, BASIC_DATA + CODE_HASH" },
        new object[] { Array.Empty<byte>(), (byte)128, 128, "empty map, the header's code chunks" },
        new object[] { Array.Empty<byte>(), (byte)0, 256, "empty map, a full stem of chunks" },
        new object[] { new byte[] { 5 }, (byte)5, 1, "run overwrites the only leaf" },
        new object[] { new byte[] { 9 }, (byte)5, 1, "run misses the only leaf" },
        new object[] { Array.Empty<byte>(), (byte)0, 4, "run fills a fixed variant exactly" },
        new object[] { Array.Empty<byte>(), (byte)0, 5, "run is one past a fixed variant" },
        new object[] { Array.Empty<byte>(), (byte)0, 8, "run fills a fixed variant exactly" },
        new object[] { Array.Empty<byte>(), (byte)0, 9, "run is one past a fixed variant" },
        new object[] { Array.Empty<byte>(), (byte)0, 16, "run fills the largest fixed variant exactly" },
        new object[] { Array.Empty<byte>(), (byte)0, 17, "run is one past the largest fixed variant" },
        new object[] { new byte[] { 0, 1, 2, 3 }, (byte)4, 4, "run sits entirely above the map" },
        new object[] { new byte[] { 4, 5, 6, 7 }, (byte)0, 4, "run sits entirely below the map" },
        new object[] { new byte[] { 0, 1, 3, 5, 7, 9 }, (byte)2, 4, "run straddles the map" },
        new object[] { new byte[] { 0, 1, 2, 3 }, (byte)0, 4, "run overwrites a full fixed variant" },
        new object[] { new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, (byte)0, 8, "run overwrites a full fixed variant" },
        new object[] { new byte[] { 0, 1 }, (byte)2, 12, "run promotes a fixed variant two tiers up" },
        new object[] { new byte[] { 0, 50, 150, 250 }, (byte)100, 100, "run straddles the large variant" },
        new object[] { new byte[] { 255 }, (byte)0, 255, "run stops one short of the map's only leaf" },
    ];

    /// <summary>
    /// A run written through <see cref="IPbtStemChanges.SetRange"/> must land exactly as the same writes
    /// made one at a time would — same variant, same order, same values — whatever the map already holds.
    /// </summary>
    [TestCaseSource(nameof(RunCases))]
    public void SetRangeMatchesPerLeafSet(byte[] seeded, byte start, int runLength, string because)
    {
        (IPbtStemChanges viaRange, IPbtStemChanges perLeaf) = ApplyRunBothWays(seeded, start, runLength);
        AssertSameWrites(viaRange, perLeaf, because);
    }

    /// <summary>
    /// Fuzzes the run merge — the sub-index binary searches, the block copies and the tier promotion —
    /// against the same writes made one at a time, over runs at every position and length and maps holding
    /// anything from nothing to a full stem.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void SetRangeMatchesPerLeafSetUnderRandomRuns(int seed)
    {
        Random rng = new(seed);

        for (int iteration = 0; iteration < 500; iteration++)
        {
            HashSet<byte> seededSet = [];
            int seedCount = rng.Next(0, 20);
            for (int i = 0; i < seedCount; i++) seededSet.Add((byte)rng.Next(256));

            byte start = (byte)rng.Next(256);
            int runLength = rng.Next(1, 256 - start + 1);

            byte[] seeded = [.. seededSet];
            (IPbtStemChanges viaRange, IPbtStemChanges perLeaf) = ApplyRunBothWays(seeded, start, runLength);
            AssertSameWrites(viaRange, perLeaf, $"seeded [{string.Join(',', seeded)}], run [{start}, {start + runLength})");
        }
    }

    /// <summary>Seeds two maps identically, then applies the same run to one as a range and to the other leaf by leaf.</summary>
    private static (IPbtStemChanges ViaRange, IPbtStemChanges PerLeaf) ApplyRunBothWays(byte[] seeded, byte start, int runLength)
    {
        IPbtStemChanges viaRange = PbtStemChanges.Rent();
        IPbtStemChanges perLeaf = PbtStemChanges.Rent();
        foreach (byte subIndex in seeded)
        {
            // seeded values are out of the run's range, so a seed that wrongly survives is visible
            viaRange = viaRange.Set(subIndex, Value(9000 + subIndex));
            perLeaf = perLeaf.Set(subIndex, Value(9000 + subIndex));
        }

        byte[] values = new byte[runLength * ValueHash256.MemorySize];
        for (int i = 0; i < runLength; i++)
        {
            ValueHash256 value = Value(start + i + 1);
            value.Bytes.CopyTo(values.AsSpan(i * ValueHash256.MemorySize));
            perLeaf = perLeaf.Set((byte)(start + i), value);
        }

        viaRange = viaRange.SetRange(start, values);
        return (viaRange, perLeaf);
    }

    private static void AssertSameWrites(IPbtStemChanges viaRange, IPbtStemChanges perLeaf, string because)
    {
        Assert.That(viaRange, Is.InstanceOf(perLeaf.GetType()), $"variant — {because}");
        Assert.That(viaRange.Count, Is.EqualTo(perLeaf.Count), $"count — {because}");
        for (int i = 0; i < perLeaf.Count; i++)
        {
            Assert.That(viaRange.SubIndexAt(i), Is.EqualTo(perLeaf.SubIndexAt(i)), $"sub-index at {i} — {because}");
            Assert.That(viaRange.Get(i), Is.EqualTo(perLeaf.Get(i)), $"value at {i} — {because}");
        }

        PbtStemChanges.Return(viaRange);
        PbtStemChanges.Return(perLeaf);
    }

    private static ValueHash256 Value(int seed)
    {
        Span<byte> bytes = stackalloc byte[32];
        bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, seed);
        bytes[31] = 0xAB; // keep it non-zero so it is not mistaken for a clear
        return new ValueHash256(bytes);
    }
}
