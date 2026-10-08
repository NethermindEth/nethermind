// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.EraE.Archive;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Archive;

/// <summary>
/// Tests for <see cref="EraSlimReceiptDecoder"/> covering the go-ethereum 4-field ERA receipt
/// format, which is what ethpandaops and go-ethereum-based providers emit.
/// </summary>
internal class EraSlimReceiptDecoderTests
{
    // go-ethereum 4-field format: outer_list { receipt_list { tx_type, status, gas, logs } }
    // Pre-Byzantium: the "status" field is a 32-byte PostTransactionState (state root), not a status code.
    // Post-Byzantium (EIP-658): the "status" field is 0x00 or 0x01 (1 byte).

    // Arbitrary - no test reads it back, it only has to be a well-formed cumulative gas item.
    private const ulong GasUsedTotal = 21000;

    // Far more receipts than the bytes declaring them could hold.
    private const int UnbackedReceiptCount = 1_000;

    // The cheapest item a receipt count can be built from, and one no decoder accepts as a receipt.
    private static readonly byte[] UnbackedReceipt = [0xc0];

    // ["", "", "", []] - the smallest slim receipt: tx type, status, cumulative gas and no logs.
    private static readonly byte[] MinimalReceipt = [0xc4, 0x80, 0x80, 0x80, 0xc0];

    [Test]
    public void Decode_GethFormat_PreByzantium_SetsPostTransactionStateNotStatusCode()
    {
        // Arrange: build raw go-ethereum 4-field receipt bytes with a 32-byte state root.
        // Receipt structure: LIST { LIST { BYTES("") [tx_type], BYTES(<32>) [state_root], INT(0) [gas], LIST{} [logs] } }
        Hash256 expectedStateRoot = TestItem.KeccakA;

        byte[] stateRootEncoded = new byte[33];      // 0xa0 + 32 bytes
        stateRootEncoded[0] = 0xa0;                   // RLP prefix for 32-byte string
        expectedStateRoot.Bytes.CopyTo(stateRootEncoded.AsSpan(1));

        // Receipt content: tx_type(0x80) + state_root(33) + gas(0x80) + logs(0xc0) = 36 bytes
        byte[] encoded = WrapAsGethReceipt([0x80, .. stateRootEncoded, 0x80, 0xc0]);

        EraSlimReceiptDecoder sut = new();
        TxReceipt[] receipts = sut.Decode(encoded.AsMemory());

        Assert.That(receipts, Has.Length.EqualTo(1));
        Assert.That(receipts[0].PostTransactionState, Is.EqualTo(expectedStateRoot), "pre-Byzantium go-ethereum receipts encode the state root in the status field; " +
            "the decoder must restore PostTransactionState, not StatusCode");
        Assert.That(receipts[0].StatusCode, Is.EqualTo(0), "StatusCode must not be set for pre-Byzantium receipts");
    }

    [Test]
    public void Decode_GethFormat_PostByzantiumSuccess_SetsStatusCode()
    {
        // Receipt content: tx_type(0x80) + status(0x01) + gas(0x80) + logs(0xc0) = 4 bytes
        byte[] encoded = WrapAsGethReceipt([0x80, 0x01, 0x80, 0xc0]);

        EraSlimReceiptDecoder sut = new();
        TxReceipt[] receipts = sut.Decode(encoded.AsMemory());

        Assert.That(receipts, Has.Length.EqualTo(1));
        Assert.That(receipts[0].StatusCode, Is.EqualTo(1));
        Assert.That(receipts[0].PostTransactionState, Is.Null);
    }

    [Test]
    public void Decode_GethFormat_PostByzantiumFailure_SetsStatusCode()
    {
        // status = 0x80 (empty bytes = 0/failure in go-ethereum encoding)
        byte[] encoded = WrapAsGethReceipt([0x80, 0x80, 0x80, 0xc0]);

        EraSlimReceiptDecoder sut = new();
        TxReceipt[] receipts = sut.Decode(encoded.AsMemory());

        Assert.That(receipts, Has.Length.EqualTo(1));
        Assert.That(receipts[0].StatusCode, Is.EqualTo(0));
        Assert.That(receipts[0].PostTransactionState, Is.Null);
    }

    [Test]
    public void Decode_GethFormat_TypedReceipt_SetsTxType([Values((byte)1, (byte)2, (byte)3)] byte txType)
    {
        byte[] encoded = WrapAsGethReceipt([txType, 0x01, 0x80, 0xc0]);

        EraSlimReceiptDecoder sut = new();
        TxReceipt[] receipts = sut.Decode(encoded.AsMemory());

        Assert.That(receipts, Has.Length.EqualTo(1));
        Assert.That(receipts[0].TxType, Is.EqualTo((TxType)txType));
        Assert.That(receipts[0].StatusCode, Is.EqualTo(1));
    }

    [Test]
    public void Decode_GethFormat_DecodesCumulativeGasUsed()
    {
        // gas = 100 => 0x64
        byte[] encoded = WrapAsGethReceipt([0x80, 0x01, 0x64, 0xc0]);

        EraSlimReceiptDecoder sut = new();
        TxReceipt[] receipts = sut.Decode(encoded.AsMemory());

        Assert.That(receipts, Has.Length.EqualTo(1));
        Assert.That(receipts[0].GasUsedTotal, Is.EqualTo(100));
    }

    [Test]
    public void Decode_GethFormat_InvalidStatusLength_Throws()
    {
        // status = 2-byte string: 0x82 0x01 0x02
        byte[] encoded = WrapAsGethReceipt([0x80, 0x82, 0x01, 0x02, 0x80, 0xc0]);

        EraSlimReceiptDecoder sut = new();
        Action act = () => sut.Decode(encoded.AsMemory());

        Assert.That(act, Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Decode_GethFormat_InvalidTxTypeLength_Throws()
    {
        // tx_type = 2-byte string
        byte[] encoded = WrapAsGethReceipt([0x82, 0x01, 0x02, 0x01, 0x80, 0xc0]);

        EraSlimReceiptDecoder sut = new();
        Action act = () => sut.Decode(encoded.AsMemory());

        Assert.That(act, Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Decode_GethFormat_LogCountTheArchiveCannotHold_Throws()
    {
        byte[] encoded = EncodeGethReceiptArchive(ReceiptRlpBuilder.Repeat(ReceiptRlpBuilder.UnbackedLogCount));

        Action act = () => new EraSlimReceiptDecoder().Decode(encoded.AsMemory());

        Assert.That(act, Throws.TypeOf<RlpLimitException>());
    }

    [Test]
    public void Decode_GethFormat_LogListOfSmallestPossibleEntries_Decodes()
    {
        byte[] encoded = EncodeGethReceiptArchive(ReceiptRlpBuilder.Repeat(ReceiptRlpBuilder.UnbackedLogCount, ReceiptRlpBuilder.MinimalLog()));

        TxReceipt[] receipts = new EraSlimReceiptDecoder().Decode(encoded.AsMemory());

        Assert.That(receipts[0].Logs, Has.Length.EqualTo(ReceiptRlpBuilder.UnbackedLogCount));
    }

    [Test]
    public void Decode_GethFormat_ReceiptCountTheArchiveCannotHold_Throws()
    {
        byte[] encoded = RepeatInSequence(UnbackedReceipt, UnbackedReceiptCount);

        Action act = () => new EraSlimReceiptDecoder().Decode(encoded.AsMemory());

        Assert.That(act, Throws.TypeOf<RlpLimitException>());
    }

    [Test]
    public void Decode_GethFormat_ReceiptListOfSmallestPossibleEntries_Decodes()
    {
        byte[] encoded = RepeatInSequence(MinimalReceipt, UnbackedReceiptCount);

        TxReceipt[] receipts = new EraSlimReceiptDecoder().Decode(encoded.AsMemory());

        Assert.That(receipts, Has.Length.EqualTo(UnbackedReceiptCount));
    }

    /// <summary>Builds an archive of identical, already-encoded receipts.</summary>
    private static byte[] RepeatInSequence(ReadOnlySpan<byte> receipt, int receiptCount)
    {
        int contentLength = receipt.Length * receiptCount;
        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        for (int i = 0; i < receiptCount; i++)
        {
            receipt.CopyTo(bytes.AsSpan(writer.Position + i * receipt.Length));
        }

        return bytes;
    }

    /// <summary>
    /// Builds a one-receipt archive whose log count can be declared without materialising the logs.
    /// </summary>
    /// <remarks>
    /// <see cref="WrapAsGethReceipt"/> cannot serve here - it only emits one-byte sequence prefixes,
    /// so it caps a receipt at 55 bytes. The slim body also differs from the eth/63 and eth/69 receipts
    /// <see cref="ReceiptRlpBuilder"/> encodes, hence the local writer.
    /// </remarks>
    private static byte[] EncodeGethReceiptArchive(ReadOnlySpan<LogEntry?> logs)
    {
        LogEntryDecoder logEntryDecoder = LogEntryDecoder.Instance;
        int logsLength = 0;
        foreach (LogEntry? log in logs)
        {
            logsLength += logEntryDecoder.GetLength(log);
        }

        int receiptLength = Rlp.LengthOf((byte)TxType.Legacy)
                            + Rlp.LengthOf((byte)1)
                            + Rlp.LengthOf(GasUsedTotal)
                            + Rlp.LengthOfSequence(logsLength);
        int outerLength = Rlp.LengthOfSequence(receiptLength);

        byte[] bytes = new byte[Rlp.LengthOfSequence(outerLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(outerLength);
        writer.StartSequence(receiptLength);
        writer.Encode((byte)TxType.Legacy);
        writer.Encode((byte)1);
        writer.Encode(GasUsedTotal);
        writer.StartSequence(logsLength);
        foreach (LogEntry? log in logs)
        {
            logEntryDecoder.Encode(ref writer, log);
        }

        return bytes;
    }

    // go-ethereum receipt encoding: outer_list { receipt_list { content } }
    private static byte[] WrapAsGethReceipt(byte[] receiptContent)
    {
        byte[] receipt = [(byte)(0xc0 + receiptContent.Length), .. receiptContent];
        return [(byte)(0xc0 + receipt.Length), .. receipt];
    }
}
