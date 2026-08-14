// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
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

    [TestCase((byte)1)]
    [TestCase((byte)2)]
    [TestCase((byte)3)]
    public void Decode_GethFormat_TypedReceipt_SetsTxType(byte txType)
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

    // go-ethereum receipt encoding: outer_list { receipt_list { content } }
    private static byte[] WrapAsGethReceipt(byte[] receiptContent)
    {
        byte[] receipt = [(byte)(0xc0 + receiptContent.Length), .. receiptContent];
        return [(byte)(0xc0 + receipt.Length), .. receipt];
    }
}
