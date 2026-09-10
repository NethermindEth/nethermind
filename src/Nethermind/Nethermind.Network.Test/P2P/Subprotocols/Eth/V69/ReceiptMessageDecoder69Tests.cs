// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture]
public class ReceiptMessageDecoder69Tests
{
    private const byte ShortSequenceHeaderBase = 0xc0;

    [Test]
    public void Can_roundtrip_receipt()
    {
        TxReceipt receipt = new()
        {
            TxType = TxType.EIP1559,
            StatusCode = 1,
            GasUsedTotal = 21000,
            Bloom = new Bloom(),
            Logs = []
        };

        ReceiptMessageDecoder69 decoder = new();
        int length = decoder.GetLength(receipt, RlpBehaviors.Eip658Receipts);
        byte[] encoded = new byte[length];
        RlpWriter writer = new(encoded);
        decoder.Encode(ref writer, receipt, RlpBehaviors.Eip658Receipts);

        RlpReader context = new(encoded);
        TxReceipt? decoded = decoder.Decode(ref context, RlpBehaviors.Eip658Receipts);

        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!.TxType, Is.EqualTo(receipt.TxType));
        Assert.That(decoded.StatusCode, Is.EqualTo(receipt.StatusCode));
        Assert.That(decoded.GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal));
    }

    [Test]
    public void Decode_throws_on_null_log_entry()
    {
        byte[] encoded = EncodeReceiptWithNullLogEntry();
        ReceiptMessageDecoder69 decoder = new();

        Assert.That(Decode, Throws.TypeOf<RlpException>());

        void Decode()
        {
            RlpReader context = new(encoded);
            decoder.Decode(ref context, RlpBehaviors.Eip658Receipts);
        }
    }

    [Test]
    public void Encoding_throws_on_null_logs([Values("length", "encode")] string operation)
    {
        TxReceipt receipt = new() { Logs = null };
        ReceiptMessageDecoder69 decoder = new();

        Assert.That(Execute, Throws.TypeOf<RlpException>());

        void Execute()
        {
            if (operation == "length")
            {
                decoder.GetLength(receipt);
                return;
            }

            RlpWriter writer = new(new byte[64]);
            decoder.Encode(ref writer, receipt);
        }
    }

    // The item count only requires a log to start before the declared end, so an under-declared logs header
    // consumes the same bytes as the canonical one and every checkpoint one level out still holds.
    [TestCase(false, TestName = "Decode_CanonicalLogsHeader_IsAccepted")]
    [TestCase(true, TestName = "Decode_UnderDeclaredLogsHeader_Throws")]
    public void Decode_LogsHeaderMustMatchItsContent(bool underDeclare)
    {
        LogEntry log = new(TestItem.AddressB, [], []);
        TxReceipt receipt = new()
        {
            TxType = TxType.EIP1559,
            StatusCode = 1,
            GasUsedTotal = 21000,
            Bloom = new Bloom(),
            Logs = [log]
        };

        AssertHeaderMustMatchItsContent(receipt, encoded => LogsHeaderIndex(encoded, Rlp.LengthOf(log)), underDeclare,
            static decoded => Assert.That(decoded.Logs, Has.Length.EqualTo(1)));
    }

    /// <summary>Asserts that the receipt decodes when the list header <paramref name="headerIndex"/> locates
    /// is canonical, and that it is rejected when that header under-declares its content by one byte.</summary>
    private static void AssertHeaderMustMatchItsContent(
        TxReceipt receipt, Func<byte[], int> headerIndex, bool underDeclare, Action<TxReceipt> assertDecoded)
    {
        ReceiptMessageDecoder69 decoder = new();
        byte[] encoded = Encode(decoder, receipt);
        int index = headerIndex(encoded);

        if (underDeclare)
        {
            encoded[index]--;
            Assert.That(() => Decode(decoder, encoded), Throws.InstanceOf<RlpException>());
        }
        else
        {
            assertDecoded(Decode(decoder, encoded));
        }
    }

    /// <summary>Locates the header of the logs list holding the single log the receipt carries.</summary>
    /// <remarks>The logs are the last item of the payload, so the declared payload end places their header
    /// exactly. A byte scan could instead hit an unrelated field and still throw, passing for the wrong
    /// reason; the header assertion catches any drift in that layout.</remarks>
    private static int LogsHeaderIndex(byte[] encoded, int logsContentLength)
    {
        RlpReader locator = new(encoded);
        int payloadEnd = locator.ReadSequenceLength() + locator.Position;
        int headerIndex = payloadEnd - logsContentLength - 1;
        Assert.That(encoded[headerIndex], Is.EqualTo((byte)(ShortSequenceHeaderBase + logsContentLength)),
            "the logs list header is not where the payload layout puts it");
        return headerIndex;
    }

    [Test]
    public void Roundtrip_FrameTxReceipt_PreservesPayerAndFrameReceipts()
    {
        TxReceipt receipt = FrameTxReceipt();
        ReceiptMessageDecoder69 decoder = new();

        TxReceipt decoded = Decode(decoder, Encode(decoder, receipt));

        Assert.That(decoded.TxType, Is.EqualTo(TxType.FrameTx));
        Assert.That(decoded.GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal));
        Assert.That(decoded.Payer, Is.EqualTo(receipt.Payer));
        Assert.That(decoded.StatusCode, Is.EqualTo(TxFrameReceipt.StatusFailure),
            "the payload carries no transaction status, so it must be derived from the frame statuses");
        AssertFrameReceiptsEqual(decoded.FrameReceipts!, receipt.FrameReceipts!);
        AssertLogsEqual(decoded.Logs!, TxFrameReceipt.ConcatLogs(receipt.FrameReceipts!));
    }

    // A receipt taken off eth/69+ is fed straight to the frame-aware consensus encoder to recompute the block's
    // receipt root, so anything the transport drops from a frame receipt fails receipt-root validation on sync.
    [Test]
    public void Roundtrip_FrameTxReceipt_AgreesWithConsensusReceiptRoot()
    {
        TxReceipt receipt = FrameTxReceipt();
        ReceiptMessageDecoder69 transportDecoder = new();

        TxReceipt decoded = Decode(transportDecoder, Encode(transportDecoder, receipt));

        ReceiptMessageDecoder consensusDecoder = new();
        IReceiptSpec spec = Cancun.Instance;
        Assert.That(ReceiptTrie.CalculateRoot(spec, [decoded], consensusDecoder),
            Is.EqualTo(ReceiptTrie.CalculateRoot(spec, [receipt], consensusDecoder)));
    }

    // The frame count only requires a frame to start before the declared end, so an under-declared frames
    // header consumes the same bytes as the canonical one and the enclosing receipt checkpoint still holds.
    [TestCase(false, TestName = "Decode_CanonicalFramesHeader_IsAccepted")]
    [TestCase(true, TestName = "Decode_UnderDeclaredFramesHeader_Throws")]
    public void Decode_FramesHeaderMustMatchItsContent(bool underDeclare)
    {
        TxFrameReceipt[] frameReceipts = [new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 0, 0, [])];
        TxReceipt receipt = new()
        {
            TxType = TxType.FrameTx,
            GasUsedTotal = 21_000,
            Payer = TestItem.AddressA,
            FrameReceipts = frameReceipts,
            StatusCode = TxFrameReceipt.AggregateStatus(frameReceipts),
            Logs = TxFrameReceipt.ConcatLogs(frameReceipts),
        };

        AssertHeaderMustMatchItsContent(receipt, FramesHeaderIndex, underDeclare,
            static decoded => Assert.That(decoded.FrameReceipts, Has.Length.EqualTo(1)));
    }

    /// <summary>Locates the header of the frames list, and asserts that the list runs to the payload end.</summary>
    /// <remarks>Walking the payload fields places the header exactly, where a byte scan could hit an unrelated
    /// field and make the under-declared case throw for the wrong reason.</remarks>
    private static int FramesHeaderIndex(byte[] encoded)
    {
        RlpReader locator = new(encoded);
        int payloadEnd = locator.ReadSequenceLength() + locator.Position;
        locator.DecodeByte();
        locator.DecodeULong();
        locator.DecodeAddress();

        int headerIndex = locator.Position;
        int framesContentLength = locator.ReadSequenceLength();
        Assert.That(locator.Position + framesContentLength, Is.EqualTo(payloadEnd),
            "the frames list is not the last item of the payload");
        Assert.That(encoded[headerIndex], Is.EqualTo((byte)(ShortSequenceHeaderBase + framesContentLength)),
            "the frames list header is not a single byte");
        return headerIndex;
    }

    private static byte[] Encode(ReceiptMessageDecoder69 decoder, TxReceipt receipt)
    {
        byte[] encoded = new byte[decoder.GetLength(receipt, RlpBehaviors.Eip658Receipts)];
        RlpWriter writer = new(encoded);
        decoder.Encode(ref writer, receipt, RlpBehaviors.Eip658Receipts);
        return encoded;
    }

    private static TxReceipt FrameTxReceipt()
    {
        TxFrameReceipt[] frameReceipts =
        [
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [Log(0x01), Log(0x02)]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, [Log(0x03)]),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, 0, []),
        ];

        return new TxReceipt
        {
            TxType = TxType.FrameTx,
            GasUsedTotal = 56_000,
            Payer = TestItem.AddressA,
            FrameReceipts = frameReceipts,
            StatusCode = TxFrameReceipt.AggregateStatus(frameReceipts),
            Logs = TxFrameReceipt.ConcatLogs(frameReceipts),
        };
    }

    private static LogEntry Log(byte marker) =>
        new(TestItem.AddressB, [marker], [Keccak.Compute([marker])]);

    private static void AssertFrameReceiptsEqual(TxFrameReceipt[] actual, TxFrameReceipt[] expected)
    {
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Status, Is.EqualTo(expected[i].Status), $"frame receipt {i} status");
            Assert.That(actual[i].ExecutionGasUsed, Is.EqualTo(expected[i].ExecutionGasUsed), $"frame receipt {i} execution gas used");
            Assert.That(actual[i].StateGasUsed, Is.EqualTo(expected[i].StateGasUsed), $"frame receipt {i} state gas used");
            AssertLogsEqual(actual[i].Logs, expected[i].Logs);
        }
    }

    // LogEntry has no value equality, so logs are compared field by field.
    private static void AssertLogsEqual(LogEntry[] actual, LogEntry[] expected)
    {
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Address, Is.EqualTo(expected[i].Address), $"log {i} address");
            Assert.That(actual[i].Data, Is.EqualTo(expected[i].Data), $"log {i} data");
            Assert.That(actual[i].Topics, Is.EqualTo(expected[i].Topics), $"log {i} topics");
        }
    }

    private static TxReceipt Decode(ReceiptMessageDecoder69 decoder, byte[] encoded)
    {
        RlpReader reader = new(encoded);
        return decoder.Decode(ref reader, RlpBehaviors.Eip658Receipts)!;
    }

    private static byte[] EncodeReceiptWithNullLogEntry()
    {
        int logsLength = Rlp.OfEmptyList.Length;
        int contentLength = Rlp.LengthOf((byte)TxType.EIP1559)
            + Rlp.LengthOf((byte)1)
            + Rlp.LengthOf(21000UL)
            + Rlp.LengthOfSequence(logsLength);
        byte[] encoded = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(encoded);
        writer.StartSequence(contentLength);
        writer.Encode((byte)TxType.EIP1559);
        writer.Encode((byte)1);
        writer.Encode(21000UL);
        writer.StartSequence(logsLength);
        writer.EncodeNullObject();
        return encoded;
    }
}
