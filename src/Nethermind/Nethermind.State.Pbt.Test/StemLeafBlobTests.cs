// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class StemLeafBlobTests
{
    [Test]
    public void ApplyReadBackClearAndDeletionSignal()
    {
        byte[] value5 = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] value200 = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

        byte[] blob = Apply([], new Dictionary<byte, byte[]?> { [5] = value5, [200] = value200 }, out _);
        Assert.That(blob, Has.Length.EqualTo(32 + 17 * (2 + 32)));
        Assert.That(StemLeafBlob.TryGetValue(blob, 5, out ReadOnlySpan<byte> read5) && read5.SequenceEqual(value5));
        Assert.That(StemLeafBlob.TryGetValue(blob, 200, out ReadOnlySpan<byte> read200) && read200.SequenceEqual(value200));
        Assert.That(StemLeafBlob.TryGetValue(blob, 6, out _), Is.False);

        // an empty value clears, untouched leaves survive
        blob = Apply(blob, new Dictionary<byte, byte[]?> { [5] = null }, out _);
        Assert.That(StemLeafBlob.TryGetValue(blob, 5, out _), Is.False);
        Assert.That(StemLeafBlob.TryGetValue(blob, 200, out read200) && read200.SequenceEqual(value200));

        // an all-zero value clears too, and an empty blob signals stem deletion with a zero root
        blob = Apply(blob, new Dictionary<byte, byte[]?> { [200] = new byte[32] }, out ValueHash256 emptyRoot);
        Assert.That(blob, Is.Empty);
        Assert.That(emptyRoot, Is.EqualTo(default(ValueHash256)));
    }

    [TestCase(1, 1)]
    [TestCase(7, 40)]
    [TestCase(99, 256)]
    public void SubtreeRootAndStemNodeHashMatchEipReference(int seed, int leafCount)
    {
        Random rng = new(seed);
        Stem stem = new(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddee"));

        Dictionary<byte, byte[]?> changes = [];
        EipReferenceTree reference = new();
        for (int i = 0; i < leafCount; i++)
        {
            byte subIndex = (byte)rng.Next(256);
            byte[] value = new byte[32];
            rng.NextBytes(value);
            changes[subIndex] = value;
        }

        foreach ((byte subIndex, byte[]? value) in changes)
        {
            reference.Insert([.. stem.Bytes, subIndex], value!);
        }

        Apply([], changes, out ValueHash256 subtreeRoot);

        // a single-stem reference tree's root is exactly the stem node hash
        ValueHash256 stemNodeHash = StemLeafBlob.ComputeStemNodeHash(stem, subtreeRoot);
        Assert.That(stemNodeHash, Is.EqualTo(new ValueHash256(reference.Merkelize())));
    }

    [TestCase(3)]
    [TestCase(17)]
    [TestCase(101)]
    public void IncrementalApplyMatchesFromScratchAndEipReference(int seed)
    {
        Random rng = new(seed);
        Stem stem = new(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddee"));
        Dictionary<byte, byte[]?> finalValues = [];
        byte[] incrementalBlob = [];
        ValueHash256 incrementalRoot = default;

        for (int batchIndex = 0; batchIndex < 6; batchIndex++)
        {
            Dictionary<byte, byte[]?> batch = [];
            for (int operation = 0; operation < 64; operation++)
            {
                byte subIndex = (byte)rng.Next(2, 256);
                batch[subIndex] = batchIndex != 0 && rng.Next(4) == 0 ? null : RandomValue(rng);
            }

            if (batchIndex == 0) batch[0] = RandomValue(rng);
            if (batchIndex == 1) batch[0] = null;
            batch[255] = RandomValue(rng);
            incrementalBlob = Apply(incrementalBlob, batch, out incrementalRoot);
            foreach ((byte subIndex, byte[]? value) in batch)
            {
                if (value is null)
                {
                    finalValues.Remove(subIndex);
                }
                else
                {
                    finalValues[subIndex] = value;
                }
            }
        }

        byte[] fromScratchBlob = Apply([], finalValues, out ValueHash256 fromScratchRoot);
        ValueHash256 oracleRoot = BuildOracleRoot(stem, finalValues);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(incrementalBlob, Is.EqualTo(fromScratchBlob));
            Assert.That(incrementalRoot, Is.EqualTo(fromScratchRoot));
            Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, incrementalRoot), Is.EqualTo(oracleRoot));
        }

        foreach ((byte subIndex, byte[]? expected) in finalValues)
        {
            Assert.That(
                StemLeafBlob.TryGetValue(incrementalBlob, subIndex, out ReadOnlySpan<byte> actual) && actual.SequenceEqual(expected),
                Is.True,
                $"sub-index {subIndex}");
        }
    }

    [Test]
    public void SingleLeafChangeOnDenseStemMatchesEipReference()
    {
        Random rng = new(2026);
        Stem stem = new(Bytes.FromHexString("0xff112233445566778899aabbccddeeff00112233445566778899aabbccddee"));
        Dictionary<byte, byte[]?> values = [];
        for (int subIndex = 0; subIndex < 256; subIndex++)
        {
            values[(byte)subIndex] = RandomValue(rng);
        }

        byte[] blob = Apply([], values, out _);
        byte[] changedValue = RandomValue(rng);
        values[117] = changedValue;
        blob = Apply(blob, new Dictionary<byte, byte[]?> { [117] = changedValue }, out ValueHash256 subtreeRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, subtreeRoot), Is.EqualTo(BuildOracleRoot(stem, values)));
            Assert.That(StemLeafBlob.TryGetValue(blob, 117, out ReadOnlySpan<byte> actual) && actual.SequenceEqual(changedValue));
        }
    }

    private static ValueHash256 BuildOracleRoot(Stem stem, Dictionary<byte, byte[]?> values)
    {
        EipReferenceTree reference = new();
        foreach ((byte subIndex, byte[]? value) in values)
        {
            reference.Insert([.. stem.Bytes, subIndex], value!);
        }

        return new ValueHash256(reference.Merkelize());
    }

    private static byte[] RandomValue(Random rng)
    {
        byte[] value = new byte[32];
        rng.NextBytes(value);
        value[0] |= 1;
        return value;
    }

    /// <summary>Maps the byte[] changes to 32-byte leaf values (null/empty = clear) and applies them.</summary>
    private static byte[] Apply(ReadOnlySpan<byte> prior, Dictionary<byte, byte[]?> changes, out ValueHash256 subtreeRoot)
    {
        Dictionary<byte, ValueHash256> mapped = [];
        foreach ((byte subIndex, byte[]? value) in changes)
        {
            ValueHash256 leaf = default;
            value?.CopyTo(leaf.BytesAsSpan);
            mapped[subIndex] = leaf;
        }

        return StemLeafBlob.Apply(prior, mapped, out subtreeRoot);
    }
}
