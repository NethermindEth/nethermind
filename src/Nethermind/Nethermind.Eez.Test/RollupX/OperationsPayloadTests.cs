// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class OperationsPayloadTests
{
    private static readonly Address Ones = new("0x1111111111111111111111111111111111111111");
    private static readonly Address Twos = new("0x2222222222222222222222222222222222222222");

    [TestCaseSource(nameof(EmptyVectors))]
    public void Encode_EmptyBlocks_MatchesTheSharedVector(uint blocks, string operations)
    {
        byte[] expected = Bytes.FromHexString(operations);

        byte[] encoded = OperationsPayload.Encode([Empty(blocks)]);
        OperationsPayload decoded = OperationsPayload.Decode(expected);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(expected), "the encoder writes the published bytes");
            Assert.That(decoded.BlockCount, Is.EqualTo(blocks), "the decoder covers every declared block");
            Assert.That(decoded.Runs().Count(), Is.EqualTo(1), "an empty span with one beneficiary is one run, however many blocks");
        }
    }

    [Test]
    public void Decode_SixHundredEmptyBlocks_ReadsThePublishedBody()
    {
        byte[] wire = [0x00, 0xd8, 0x04, 0xff, 0xd8, 0x04, 0xd8, 0x04, .. Enumerable.Repeat((byte)0x11, 20), 0xd8, 0x04, 0x00];

        OperationsPayload decoded = OperationsPayload.Decode(wire);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wire.Length, Is.EqualTo(31), "precondition: the published fixture is 31 bytes");
            Assert.That(decoded.BlockCount, Is.EqualTo(600));
            Assert.That(OperationsPayload.Encode(decoded.Runs().ToArray()), Is.EqualTo(wire), "the body re-encodes byte for byte");
        }
    }

    [Test]
    public void Encode_EmptyBlockThenBlockWithTransaction_WritesEachColumnInOrder()
    {
        byte[] transaction = Transaction(19);
        OperationsRun[] runs = [Empty(1), new(1, Twos, new byte[] { 7 }, [transaction])];

        byte[] encoded = OperationsPayload.Encode(runs);
        OperationsRun[] decoded = OperationsPayload.Decode(encoded).Runs().ToArray();

        byte[] expected = [0x00, 0x02, 0x00, 0x01, 0x01, .. Ones.Bytes, 0x01, .. Twos.Bytes, 0x01, 0x00, 0x01, 0x01, 0x07,
            .. Varint(transaction.Length), .. transaction];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(expected), "counts, beneficiaries, extra data, lengths, then transactions");
            Assert.That(decoded.Length, Is.EqualTo(2));
            Assert.That(decoded[1].Beneficiary, Is.EqualTo(Twos));
            Assert.That(decoded[1].ExtraData.ToArray(), Is.EqualTo(new byte[] { 7 }));
            Assert.That(decoded[1].Transactions.Single().ToArray(), Is.EqualTo(transaction));
        }
    }

    [Test]
    public void Encode_MetadataChangesInsideEmptyBlocks_KeepsOneEmptyToken()
    {
        OperationsRun[] runs = [Empty(3), new(5, Twos, new byte[] { 1, 2 }, []), Empty(2)];

        byte[] encoded = OperationsPayload.Encode(runs);
        OperationsRun[] decoded = OperationsPayload.Decode(encoded).Runs().ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded[2], Is.EqualTo(0xc8), "ten empty blocks are one count token despite the metadata changes");
            Assert.That(decoded.Select(static run => run.Blocks), Is.EqualTo(new uint[] { 3, 5, 2 }), "the runs split only where metadata changes");
            Assert.That(decoded[1].ExtraData.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
        }
    }

    [TestCase(128, new byte[] { 0x80, 0x80 }, TestName = "TwoByteMinimum")]
    [TestCase(1428, new byte[] { 0x85, 0x94 }, TestName = "TwoByte1428")]
    [TestCase(16_383, new byte[] { 0xbf, 0xff }, TestName = "TwoByteMaximum")]
    public void Encode_ManyTransactions_UsesATwoByteCount(int transactions, byte[] token)
    {
        byte[] transaction = Transaction(0);
        OperationsRun[] runs = [new(1, Ones, ReadOnlyMemory<byte>.Empty, Enumerable.Repeat((ReadOnlyMemory<byte>)transaction, transactions).ToArray())];

        byte[] encoded = OperationsPayload.Encode(runs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded.AsSpan(2, 2).ToArray(), Is.EqualTo(token));
            Assert.That(OperationsPayload.Decode(encoded).TransactionCount, Is.EqualTo(transactions));
        }
    }

    [TestCase(new byte[] { 0x80, 0x00 }, TestName = "TwoByteCountFitsOneByte")]
    [TestCase(new byte[] { 0x80, 0x7f }, TestName = "TwoByteCountBelow128")]
    [TestCase(new byte[] { 0xff, 0x00 }, TestName = "LongRunOfZero")]
    [TestCase(new byte[] { 0xff, 0x40 }, TestName = "LongRunFitsShortToken")]
    [TestCase(new byte[] { 0xff, 0xc1, 0x00 }, TestName = "LongRunPaddedVarint")]
    [TestCase(new byte[] { 0x80 }, TestName = "TruncatedTwoByteCount")]
    [TestCase(new byte[] { 0xff }, TestName = "TruncatedLongRun")]
    [TestCase(new byte[] { 0x00, 0x00 }, TestName = "AdjacentEmptyTokens")]
    [TestCase(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0x10 }, TestName = "LongRunOverflow")]
    public void Decode_MalformedCountToken_Throws(byte[] token)
    {
        byte[] wire = [0x00, 0x02, .. token, .. EmptyMetadata(2)];

        Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(wire));
    }

    [TestCase(new byte[] { 0x01 }, TestName = "WrongVersion")]
    [TestCase(new byte[] { 0x00, 0x00 }, TestName = "NoBlocks")]
    public void Decode_InvalidHeader_Throws(byte[] wire) =>
        Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(wire));

    [Test]
    public void Decode_TrailingByte_Throws()
    {
        byte[] wire = [.. OperationsPayload.Encode([Empty(600)]), 0x00];

        Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(wire));
    }

    [TestCase(0, TestName = "EmptyBeneficiaryRun")]
    [TestCase(3, TestName = "BeneficiaryRunTooLong")]
    public void Decode_BeneficiaryRunMiscovers_Throws(byte runLength)
    {
        byte[] wire = OperationsPayload.Encode([Empty(2)]);
        wire[3] = runLength;

        Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(wire));
    }

    [Test]
    public void Decode_AdjacentRunsWithTheSameBeneficiary_Throws()
    {
        byte[] wire = [0x00, 0x02, 0xc0, 0x01, .. Ones.Bytes, 0x01, .. Ones.Bytes, 0x02, 0x00];

        Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(wire));
    }

    [Test]
    public void Decode_ExtraDataLongerThan32Bytes_Throws()
    {
        byte[] wire = [0x00, 0x02, 0xc0, 0x02, .. Ones.Bytes, 0x02, 33, .. new byte[33]];

        Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(wire));
    }

    [TestCase(0x76, TestName = "NativeSystemTransaction")]
    [TestCase(0x03, TestName = "BlobTransaction")]
    public void Decode_UnsupportedTransactionType_Throws(byte type)
    {
        byte[] wire = [0x00, 0x01, 0x01, .. EmptyMetadata(1), 0x01, type];

        Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(wire));
    }

    [Test]
    public void Decode_EveryTruncation_Throws()
    {
        byte[] encoded = OperationsPayload.Encode([new(1, Ones, ReadOnlyMemory<byte>.Empty, [Transaction(1)])]);

        for (int end = 0; end < encoded.Length; end++)
        {
            Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(encoded.AsMemory(0, end)), $"truncated at {end}");
        }
    }

    [Test]
    public void Decode_DeclaresMoreTransactionsThanBytes_Throws()
    {
        byte[] wire = [0x00, 0x01, 0x01, .. EmptyMetadata(1)];

        Assert.Throws<EezSettlementException>(() => OperationsPayload.Decode(wire));
    }

    private static TestCaseData[] EmptyVectors() =>
    [
        new(3u, "0003c103" + new string('1', 40) + "0300") { TestName = "ThreeEmptyBlocks" },
        new(17u, "0011cf11" + new string('1', 40) + "1100") { TestName = "SeventeenEmptyBlocks" },
        new(64u, "0040fe40" + new string('1', 40) + "4000") { TestName = "SixtyFourEmptyBlocks" },
        new(300u, "00ac02ffac02ac02" + new string('1', 40) + "ac0200") { TestName = "ThreeHundredEmptyBlocks" },
    ];

    private static OperationsRun Empty(uint blocks) => new(blocks, Ones, ReadOnlyMemory<byte>.Empty, []);

    private static byte[] EmptyMetadata(uint blocks) => [.. Varint((int)blocks), .. Ones.Bytes, .. Varint((int)blocks), 0x00];

    private static byte[] Transaction(ulong nonce) => TxDecoder.Instance.Encode(Build.A.Transaction
        .WithType(TxType.EIP1559)
        .WithChainId(1)
        .WithNonce(nonce)
        .WithGasLimit(21_000)
        .WithSignature(new Signature(1, 2, 27))
        .TestObject, RlpBehaviors.SkipTypedWrapping).Bytes;

    private static byte[] Varint(int value)
    {
        List<byte> bytes = [];
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            bytes.Add((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        bytes.Add((byte)remaining);
        return bytes.ToArray();
    }
}
