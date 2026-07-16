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
        new object[] { 2, typeof(Length8StemChanges) },
        new object[] { 8, typeof(Length8StemChanges) },
        new object[] { 9, typeof(SortedStemChanges) },
        new object[] { 70, typeof(SortedStemChanges) },
    ];

    /// <summary>
    /// Across all three variants: entries are added out of order, one is updated in place and one is
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
    /// Fuzzes the SIMD insertion search (<see cref="Length8StemChanges"/>), the binary-search insert
    /// (<see cref="SortedStemChanges"/>) and the promotion path by writing a random permutation of
    /// sub-indices (with a second pass of in-place overwrites) and checking the map reproduces a
    /// <see cref="SortedDictionary{TKey,TValue}"/> reference exactly, in order.
    /// </summary>
    [TestCase(1, 1)]
    [TestCase(8, 2)]
    [TestCase(9, 3)]
    [TestCase(64, 4)]
    [TestCase(256, 5)]
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

    private static ValueHash256 Value(int seed)
    {
        Span<byte> bytes = stackalloc byte[32];
        bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, seed);
        bytes[31] = 0xAB; // keep it non-zero so it is not mistaken for a clear
        return new ValueHash256(bytes);
    }
}
