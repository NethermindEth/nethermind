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

        int logsLength = 0;
        int contentLength = Rlp.LengthOf((byte)TxType.Legacy)
            + Rlp.LengthOf((byte)1)
            + Rlp.LengthOf(gasUsedTotal)
            + Rlp.LengthOfSequence(logsLength)
            + Rlp.LengthOf(gasSpent)
            + Rlp.LengthOf(1);

        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);
        stream.Encode((byte)TxType.Legacy);
        stream.Encode((byte)1);
        stream.Encode(gasUsedTotal);
        stream.StartSequence(logsLength);
        stream.Encode(gasSpent);
        stream.Encode(1);

        ReceiptMessageDecoder69 decoder = new();
        Assert.Throws<RlpException>(() => decoder.Decode(stream.Data.ToArray().AsRlpStream(), RlpBehaviors.Eip7778Receipts));
    }
}
