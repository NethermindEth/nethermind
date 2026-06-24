// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class TransactionsMessageSerializer : IZeroInnerMessageSerializer<TransactionsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<TransactionsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(TransactionsMessage.Transactions));
        private static readonly Nethermind.Serialization.Rlp.TxDecoder TxDecoder = Nethermind.Serialization.Rlp.TxDecoder.Instance;

        public void Serialize(IByteBuffer byteBuffer, TransactionsMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            ByteBufferRlpWriter writer = new(byteBuffer);

            writer.StartSequence(contentLength);
            foreach (Transaction tx in message.Transactions.AsSpan())
            {
                TxDecoder.Encode(ref writer, tx, RlpBehaviors.InMempoolForm);
            }
        }

        public TransactionsMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        private static TransactionsMessage Deserialize(ref RlpReader ctx) =>
            new(DeserializeTxs(ref ctx));

        public int GetLength(TransactionsMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.Transactions.Count; i++)
            {
                contentLength += TxDecoder.GetLength(message.Transactions[i], RlpBehaviors.InMempoolForm);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public static IOwnedReadOnlyList<Transaction> DeserializeTxs(ref RlpReader ctx)
        {
            int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
            int length = ctx.PeekNumberOfItemsRemaining(checkPosition);
            ctx.GuardLimit(length, RlpLimit);

            ArrayPoolList<Transaction> result = new(length);
            try
            {
                for (int i = 0; i < length; i++)
                {
                    result.Add(TxDecoder.DecodeGuardNotNull(ref ctx, RlpBehaviors.InMempoolForm));
                }
                ctx.Check(checkPosition);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
    }
}
