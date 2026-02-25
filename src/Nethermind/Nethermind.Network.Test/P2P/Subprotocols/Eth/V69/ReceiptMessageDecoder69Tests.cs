// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture]
public class ReceiptMessageDecoder69Tests
{
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
        RlpStream rlpStream = new(length);
        decoder.Encode(rlpStream, receipt, RlpBehaviors.Eip658Receipts);
        byte[] encoded = rlpStream.Data!.ToArray();

        TxReceipt? decoded = decoder.Decode(encoded.AsRlpStream(), RlpBehaviors.Eip658Receipts);

        decoded.Should().NotBeNull();
        decoded!.TxType.Should().Be(receipt.TxType);
        decoded.StatusCode.Should().Be(receipt.StatusCode);
        decoded.GasUsedTotal.Should().Be(receipt.GasUsedTotal);
    }
}
