// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture]
public class ReceiptMessageDecoder69Tests
{
    [Test]
    public void Rejects_trailing_fields_after_gas_spent()
    {
        long gasUsedTotal = 1;
        long gasSpent = 2;

        RlpStream stream = BuildStream(gasUsedTotal, Rlp.LengthOf(gasSpent) + Rlp.LengthOf(1));
        stream.Encode(gasSpent);
        stream.Encode(1);

        ReceiptMessageDecoder69 decoder = new();
        Assert.Throws<RlpException>(() => decoder.Decode(stream.Data.ToArray().AsRlpStream(), RlpBehaviors.Eip7778Receipts));
    }

    [Test]
    public void Rejects_overflow_gas_spent()
    {
        long gasUsedTotal = 1;
        byte[] overflowGasSpent = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]; // 2^64

        RlpStream stream = BuildStream(gasUsedTotal, Rlp.LengthOf(overflowGasSpent));
        stream.Encode(overflowGasSpent);

        ReceiptMessageDecoder69 decoder = new();
        Assert.Throws<RlpException>(() => decoder.Decode(stream.Data.ToArray().AsRlpStream(), RlpBehaviors.Eip7778Receipts));
    }

    private static RlpStream BuildStream(long gasUsedTotal, int extraLength)
    {
        const int logsLength = 0;
        int contentLength = Rlp.LengthOf((byte)TxType.Legacy)
            + Rlp.LengthOf((byte)1)
            + Rlp.LengthOf(gasUsedTotal)
            + Rlp.LengthOfSequence(logsLength)
            + extraLength;

        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);
        stream.Encode((byte)TxType.Legacy);
        stream.Encode((byte)1);
        stream.Encode(gasUsedTotal);
        stream.StartSequence(logsLength);
        return stream;
    }
}
