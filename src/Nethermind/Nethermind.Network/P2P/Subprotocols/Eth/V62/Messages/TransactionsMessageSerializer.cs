// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class TransactionsMessageSerializer : IZeroInnerMessageSerializer<TransactionsMessage>
    {
        private TxDecoder _decoder = new();

        public void Serialize(IByteBuffer byteBuffer, TransactionsMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
            NettyRlpStream nettyRlpStream = new(byteBuffer);

            nettyRlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.Transactions.Count; i++)
            {
                nettyRlpStream.Encode(message.Transactions[i]);
            }
        }

        public TransactionsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            Transaction[] txs = DeserializeTxs(rlpStream);
            return new TransactionsMessage(txs);
        }

        public int GetLength(TransactionsMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.Transactions.Count; i++)
            {
                contentLength += _decoder.GetLength(message.Transactions[i], RlpBehaviors.None);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public Transaction[] DeserializeTxs(RlpStream rlpStream)
        {
            return Rlp.DecodeArray<Transaction>(rlpStream);
        }
    }
}
