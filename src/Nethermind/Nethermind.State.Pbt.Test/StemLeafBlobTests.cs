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

        byte[] blob = StemLeafBlob.Apply([], new Dictionary<byte, byte[]?> { [5] = value5, [200] = value200 }, out _);
        Assert.That(blob, Has.Length.EqualTo(32 + 2 * 32));
        Assert.That(StemLeafBlob.TryGetValue(blob, 5, out ReadOnlySpan<byte> read5) && read5.SequenceEqual(value5));
        Assert.That(StemLeafBlob.TryGetValue(blob, 200, out ReadOnlySpan<byte> read200) && read200.SequenceEqual(value200));
        Assert.That(StemLeafBlob.TryGetValue(blob, 6, out _), Is.False);

        // null clears, untouched leaves survive
        blob = StemLeafBlob.Apply(blob, new Dictionary<byte, byte[]?> { [5] = null }, out _);
        Assert.That(StemLeafBlob.TryGetValue(blob, 5, out _), Is.False);
        Assert.That(StemLeafBlob.TryGetValue(blob, 200, out read200) && read200.SequenceEqual(value200));

        // an all-zero value clears too, and an empty blob signals stem deletion with a zero root
        blob = StemLeafBlob.Apply(blob, new Dictionary<byte, byte[]?> { [200] = new byte[32] }, out ValueHash256 emptyRoot);
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

        StemLeafBlob.Apply([], changes, out ValueHash256 subtreeRoot);

        // a single-stem reference tree's root is exactly the stem node hash
        ValueHash256 stemNodeHash = StemLeafBlob.ComputeStemNodeHash(stem, subtreeRoot);
        Assert.That(stemNodeHash, Is.EqualTo(new ValueHash256(reference.Merkelize())));
    }
}
