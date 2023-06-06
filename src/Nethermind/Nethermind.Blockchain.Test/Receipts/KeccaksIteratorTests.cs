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
    public void TestKeccakIteratorDecodeCorrectly(Keccak[] keccak)
    {
        Keccak[] keccaks = new[] { TestItem.KeccakA, Keccak.Zero };
        Keccak[] decoded = EncodeDecode(keccaks);
        decoded.Should().BeEquivalentTo(keccaks);
    }

    [TestCaseSource(nameof(TestKeccaks))]
    public void TestKeccakIteratorDecodedCorrectlyWithReset(Keccak[] keccak)
    {
        Keccak[] keccaks = new[] { TestItem.KeccakA, Keccak.Zero };
        Keccak[] decoded = EncodeDecodeReDecoded(keccaks);
        decoded.Should().BeEquivalentTo(keccaks);
    }

    public static IEnumerable<Keccak[]> TestKeccaks()
    {
        yield return Array.Empty<Keccak>();
        yield return new[] { TestItem.KeccakA };
        yield return new[] { Keccak.Zero };
        yield return new[] { TestItem.KeccakA, Keccak.Zero };
        yield return new[] { Keccak.Zero, TestItem.KeccakA };
        yield return new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, Keccak.Zero, };
        yield return new[] { TestItem.KeccakA, new Keccak("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000") };
        yield return new[] { TestItem.KeccakA, new Keccak("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff") };
        yield return new[] { TestItem.KeccakA, new Keccak("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000"), TestItem.KeccakB };
        yield return new[] { TestItem.KeccakA, new Keccak("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff"), TestItem.KeccakB };
        yield return new[] { new Keccak("0xffffffffffffffffffffffffffffffff00000000000000000000000000000000"), TestItem.KeccakB };
        yield return new[] { new Keccak("0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff"), TestItem.KeccakB };
    }

    private Keccak[] EncodeDecode(Keccak[] input)
    {
        int totalLength = 0;
        foreach (Keccak keccak in input)
        {
            totalLength += Rlp.LengthOf(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }
        int sequenceLength = Rlp.LengthOfSequence(totalLength);

        RlpStream rlpStream = new RlpStream(sequenceLength);
        rlpStream.StartSequence(totalLength);
        foreach (Keccak keccak in input)
        {
            rlpStream.Encode(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }

        Span<byte> buffer = stackalloc byte[32];
        KeccaksIterator iterator = new(rlpStream.Data, buffer);

        List<Keccak> decoded = new();
        while (iterator.TryGetNext(out KeccakStructRef kec))
        {
            decoded.Add(kec.ToKeccak());
        }

        return decoded.ToArray();
    }

    private Keccak[] EncodeDecodeReDecoded(Keccak[] input)
    {
        int totalLength = 0;
        foreach (Keccak keccak in input)
        {
            totalLength += Rlp.LengthOf(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }
        int sequenceLength = Rlp.LengthOfSequence(totalLength);

        RlpStream rlpStream = new RlpStream(sequenceLength);
        rlpStream.StartSequence(totalLength);
        foreach (Keccak keccak in input)
        {
            rlpStream.Encode(keccak.Bytes.WithoutLeadingZerosOrEmpty());
        }

        Span<byte> buffer = stackalloc byte[32];
        KeccaksIterator iterator = new(rlpStream.Data, buffer);

        List<Keccak> decoded = new();
        while (iterator.TryGetNext(out KeccakStructRef kec))
        {
            decoded.Add(kec.ToKeccak());
        }

        decoded.Clear();
        iterator.Reset();

        while (iterator.TryGetNext(out KeccakStructRef kec))
        {
            decoded.Add(kec.ToKeccak());
        }

        return decoded.ToArray();
    }
}
