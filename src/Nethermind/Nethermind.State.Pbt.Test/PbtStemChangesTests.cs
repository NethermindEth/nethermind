// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
        new object[] { 9, typeof(DictionaryStemChanges) },
        new object[] { 70, typeof(DictionaryStemChanges) },
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

        StemLeafWrite[] sorted = new StemLeafWrite[map.Count];
        map.WriteSorted(sorted);

        for (int i = 0; i < distinctKeys; i++)
        {
            Assert.That(sorted[i].SubIndex, Is.EqualTo((byte)i), "strictly ascending by sub-index");
            ValueHash256 expected = i == 0 ? updated
                : i == 1 ? default
                : Value(i + 1);
            Assert.That(sorted[i].Value, Is.EqualTo(expected));
        }

        PbtStemChanges.Return(map);
        Assert.That(PbtStemChanges.Rent().Count, Is.Zero, "a freshly rented map is empty");
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
