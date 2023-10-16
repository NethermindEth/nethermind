// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

public class KeccaksIteratorTests
{
    [TestCaseSource(nameof(TestKeccaks))]
    public void TestKeccakIteratorDecodeCorrectly(Commitment[] keccak)
    {
        Commitment[] keccaks = new[] { TestItem.KeccakA, Keccak.Zero };
        Commitment[] decoded = EncodeDecode(keccaks);
        decoded.Should().BeEquivalentTo(keccaks);
    }

    [TestCaseSource(nameof(TestKeccaks))]
    public void TestKeccakIteratorDecodedCorrectlyWithReset(Commitment[] keccak)
    {
        Commitment[] keccaks = new[] { TestItem.KeccakA, Keccak.Zero };
        Commitment[] decoded = EncodeDecodeReDecoded(keccaks);
        decoded.Should().BeEquivalentTo(keccaks);
    }

    public static IEnumerable<Commitment[]> TestKeccaks()
    {
        yield return Array.Empty<Commitment>();
        yield return new[] { TestItem.KeccakA };
        yield return new[] { Keccak.Zero };
        yield return new[] { TestItem.KeccakA, Keccak.Zero };
        yield return new[] { Keccak.Zero, TestItem.KeccakA };
        yield return new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, Keccak.Zero, };
        yield return new[] { TestItem.KeccakA, new Commitment("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000") };
        yield return new[] { TestItem.KeccakA, new Commitment("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff") };
        yield return new[] { TestItem.KeccakA, new Commitment("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000"), TestItem.KeccakB };
        yield return new[] { TestItem.KeccakA, new Commitment("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff"), TestItem.KeccakB };
        yield return new[] { new Commitment("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000"), TestItem.KeccakB };
        yield return new[] { new Commitment("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff"), TestItem.KeccakB };
    }

    private Commitment[] EncodeDecode(Commitment[] input)
    {
        int totalLength = 0;
        foreach (Commitment keccak in input)
        {
            totalLength += Rlp.LengthOf(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }
        int sequenceLength = Rlp.LengthOfSequence(totalLength);

        RlpStream rlpStream = new RlpStream(sequenceLength);
        rlpStream.StartSequence(totalLength);
        foreach (Commitment keccak in input)
        {
            rlpStream.Encode(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }

        Span<byte> buffer = stackalloc byte[32];
        KeccaksIterator iterator = new(rlpStream.Data, buffer);

        List<Commitment> decoded = new();
        while (iterator.TryGetNext(out CommitmentStructRef kec))
        {
            decoded.Add(kec.ToCommitment());
        }

        return decoded.ToArray();
    }

    private Commitment[] EncodeDecodeReDecoded(Commitment[] input)
    {
        int totalLength = 0;
        foreach (Commitment keccak in input)
        {
            totalLength += Rlp.LengthOf(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }
        int sequenceLength = Rlp.LengthOfSequence(totalLength);

        RlpStream rlpStream = new RlpStream(sequenceLength);
        rlpStream.StartSequence(totalLength);
        foreach (Commitment keccak in input)
        {
            rlpStream.Encode(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }

        Span<byte> buffer = stackalloc byte[32];
        KeccaksIterator iterator = new(rlpStream.Data, buffer);

        List<Commitment> decoded = new();
        while (iterator.TryGetNext(out CommitmentStructRef kec))
        {
            decoded.Add(kec.ToCommitment());
        }

        decoded.Clear();
        iterator.Reset();

        while (iterator.TryGetNext(out CommitmentStructRef kec))
        {
            decoded.Add(kec.ToCommitment());
        }

        return decoded.ToArray();
    }
}
