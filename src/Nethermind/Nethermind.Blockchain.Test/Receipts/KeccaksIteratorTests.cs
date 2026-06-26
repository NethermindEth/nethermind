// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[Parallelizable(ParallelScope.All)]
public class KeccaksIteratorTests
{
    [TestCaseSource(nameof(TestKeccaks))]
    public void TestKeccakIteratorDecodeCorrectly(Hash256[] keccak)
    {
        Hash256[] keccaks = new[] { TestItem.KeccakA, Keccak.Zero };
        Hash256[] decoded = EncodeDecode(keccaks);
        Assert.That(decoded, Is.EqualTo(keccaks));
    }

    [TestCaseSource(nameof(TestKeccaks))]
    public void TestKeccakIteratorDecodedCorrectlyWithReset(Hash256[] keccak)
    {
        Hash256[] keccaks = new[] { TestItem.KeccakA, Keccak.Zero };
        Hash256[] decoded = EncodeDecodeReDecoded(keccaks);
        Assert.That(decoded, Is.EqualTo(keccaks));
    }

    public static IEnumerable<Hash256[]> TestKeccaks()
    {
        yield return Array.Empty<Hash256>();
        yield return new[] { TestItem.KeccakA };
        yield return new[] { Keccak.Zero };
        yield return new[] { TestItem.KeccakA, Keccak.Zero };
        yield return new[] { Keccak.Zero, TestItem.KeccakA };
        yield return new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, Keccak.Zero, };
        yield return new[] { TestItem.KeccakA, new Hash256("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000") };
        yield return new[] { TestItem.KeccakA, new Hash256("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff") };
        yield return new[] { TestItem.KeccakA, new Hash256("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000"), TestItem.KeccakB };
        yield return new[] { TestItem.KeccakA, new Hash256("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff"), TestItem.KeccakB };
        yield return new[] { new Hash256("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000"), TestItem.KeccakB };
        yield return new[] { new Hash256("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff"), TestItem.KeccakB };
    }

    private Hash256[] EncodeDecode(Hash256[] input)
    {
        int totalLength = 0;
        foreach (Hash256 keccak in input)
        {
            totalLength += Rlp.LengthOf(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }

        byte[] rlp = Encode(input, totalLength);

        Span<byte> buffer = stackalloc byte[32];
        KeccaksIterator iterator = new(rlp, buffer);

        List<Hash256> decoded = [];
        while (iterator.TryGetNext(out Hash256StructRef kec))
        {
            decoded.Add(kec.ToCommitment());
        }

        return decoded.ToArray();
    }

    private Hash256[] EncodeDecodeReDecoded(Hash256[] input)
    {
        int totalLength = 0;
        foreach (Hash256 keccak in input)
        {
            totalLength += Rlp.LengthOf(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }

        byte[] rlp = Encode(input, totalLength);

        Span<byte> buffer = stackalloc byte[32];
        KeccaksIterator iterator = new(rlp, buffer);

        List<Hash256> decoded = [];
        while (iterator.TryGetNext(out Hash256StructRef kec))
        {
            decoded.Add(kec.ToCommitment());
        }

        decoded.Clear();
        iterator.Reset();

        while (iterator.TryGetNext(out Hash256StructRef kec))
        {
            decoded.Add(kec.ToCommitment());
        }

        return decoded.ToArray();
    }

    private static byte[] Encode(Hash256[] input, int totalLength)
    {
        byte[] rlp = new byte[Rlp.LengthOfSequence(totalLength)];
        RlpWriter writer = new(rlp);
        writer.StartSequence(totalLength);
        foreach (Hash256 keccak in input)
        {
            writer.Encode(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }

        return rlp;
    }
}
