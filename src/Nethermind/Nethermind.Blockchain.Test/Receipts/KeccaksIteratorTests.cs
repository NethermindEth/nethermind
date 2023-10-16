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
        Commitment[] keccaks = new[] { TestItem._commitmentA, Keccak.Zero };
        Commitment[] decoded = EncodeDecode(keccaks);
        decoded.Should().BeEquivalentTo(keccaks);
    }

    [TestCaseSource(nameof(TestKeccaks))]
    public void TestKeccakIteratorDecodedCorrectlyWithReset(Commitment[] keccak)
    {
        Commitment[] keccaks = new[] { TestItem._commitmentA, Keccak.Zero };
        Commitment[] decoded = EncodeDecodeReDecoded(keccaks);
        decoded.Should().BeEquivalentTo(keccaks);
    }

    public static IEnumerable<Commitment[]> TestKeccaks()
    {
        yield return Array.Empty<Commitment>();
        yield return new[] { TestItem._commitmentA };
        yield return new[] { Keccak.Zero };
        yield return new[] { TestItem._commitmentA, Keccak.Zero };
        yield return new[] { Keccak.Zero, TestItem._commitmentA };
        yield return new[] { TestItem._commitmentA, TestItem._commitmentB, TestItem._commitmentC, Keccak.Zero, };
        yield return new[] { TestItem._commitmentA, new Commitment("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000") };
        yield return new[] { TestItem._commitmentA, new Commitment("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff") };
        yield return new[] { TestItem._commitmentA, new Commitment("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000"), TestItem._commitmentB };
        yield return new[] { TestItem._commitmentA, new Commitment("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff"), TestItem._commitmentB };
        yield return new[] { new Commitment("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000"), TestItem._commitmentB };
        yield return new[] { new Commitment("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff"), TestItem._commitmentB };
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
