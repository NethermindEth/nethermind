// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.P2P.Messages;

public class XdcPooledTransactionsMessageSerializer : IZeroInnerMessageSerializer<XdcPooledTransactionsMessage>
{
    private readonly TransactionsMessageSerializer _transactionsMessageSerializer = new();

    public void Serialize(IByteBuffer byteBuffer, XdcPooledTransactionsMessage message) =>
        _transactionsMessageSerializer.Serialize(byteBuffer, message);

    public XdcPooledTransactionsMessage Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

    private static XdcPooledTransactionsMessage Deserialize(ref RlpReader ctx) =>
        new(TransactionsMessageSerializer.DeserializeTxs(ref ctx));

    public int GetLength(XdcPooledTransactionsMessage message, out int contentLength) =>
        _transactionsMessageSerializer.GetLength(message, out contentLength);
}
