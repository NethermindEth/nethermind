// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.EraE.Archive;
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
        byte[] receiptContent = [0x80, ..stateRootEncoded, 0x80, 0xc0];
        // Receipt list: list-prefix(1) + content(36) = 37 bytes
        byte[] receipt = [(byte)(0xc0 + receiptContent.Length), ..receiptContent];
        // Outer list: list-prefix(1) + receipt(37) = 38 bytes
        byte[] encoded = [(byte)(0xc0 + receipt.Length), ..receipt];

        // Act
        EraSlimReceiptDecoder sut = new();
        TxReceipt[] receipts = sut.Decode(encoded.AsMemory());

        // Assert
        receipts.Should().HaveCount(1);
        receipts[0].PostTransactionState.Should().Be(expectedStateRoot,
            "pre-Byzantium go-ethereum receipts encode the state root in the status field; " +
            "the decoder must restore PostTransactionState, not StatusCode");
        receipts[0].StatusCode.Should().Be(0,
            "StatusCode must not be set for pre-Byzantium receipts");
    }

    [Test]
    public void Decode_GethFormat_PostByzantiumSuccess_SetsStatusCode()
    {
        // Receipt content: tx_type(0x80) + status(0x01) + gas(0x80) + logs(0xc0) = 4 bytes
        byte[] receiptContent = [0x80, 0x01, 0x80, 0xc0];
        byte[] receipt = [(byte)(0xc0 + receiptContent.Length), ..receiptContent];
        byte[] encoded = [(byte)(0xc0 + receipt.Length), ..receipt];

        EraSlimReceiptDecoder sut = new();
        TxReceipt[] receipts = sut.Decode(encoded.AsMemory());

        receipts.Should().HaveCount(1);
        receipts[0].StatusCode.Should().Be(1);
        receipts[0].PostTransactionState.Should().BeNull();
    }

    [Test]
    public void Decode_GethFormat_PostByzantiumFailure_SetsStatusCode()
    {
        // status = 0x80 (empty bytes = 0/failure in go-ethereum encoding)
        byte[] receiptContent = [0x80, 0x80, 0x80, 0xc0];
        byte[] receipt = [(byte)(0xc0 + receiptContent.Length), ..receiptContent];
        byte[] encoded = [(byte)(0xc0 + receipt.Length), ..receipt];

        EraSlimReceiptDecoder sut = new();
        TxReceipt[] receipts = sut.Decode(encoded.AsMemory());

        receipts.Should().HaveCount(1);
        receipts[0].StatusCode.Should().Be(0);
        receipts[0].PostTransactionState.Should().BeNull();
    }
}
