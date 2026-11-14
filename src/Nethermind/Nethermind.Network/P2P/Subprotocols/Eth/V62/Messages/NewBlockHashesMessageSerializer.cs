// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockHashesMessageSerializer : IMessageSerializer<NewBlockHashesMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<NewBlockHashesMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NewBlockHashesMessage.BlockHashes));

        public int Serialize(Span<byte> destination, NewBlockHashesMessage message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);

            RlpSequence sequence = writer.StartSequence(GetContentLength(message));
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                int miniContentLength = Rlp.LengthOf(message.BlockHashes[i].Item1);
                miniContentLength += Rlp.LengthOf(message.BlockHashes[i].Item2);
                RlpSequence item = writer.StartSequence(miniContentLength);
                writer.Encode(message.BlockHashes[i].Item1);
                writer.Encode(message.BlockHashes[i].Item2);
                writer.EndSequence(item);
            }

            writer.EndSequence(sequence);
            return writer.Length;
        }

        public NewBlockHashesMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            NewBlockHashesMessage message = Deserialize(ref ctx);
            ctx.CheckEnd();
            return message;
        }

        public int GetLength(NewBlockHashesMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private static int GetContentLength(NewBlockHashesMessage message)
        {
            int contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                int miniContentLength = Rlp.LengthOf(message.BlockHashes[i].Item1);
                miniContentLength += Rlp.LengthOf(message.BlockHashes[i].Item2);
                contentLength += Rlp.LengthOfSequence(miniContentLength);
            }

            return contentLength;
        }

        private static NewBlockHashesMessage Deserialize(ref RlpReader ctx)
        {
            (Hash256, ulong)[] blockHashes = ctx.DecodeNonNullArray(static (ref RlpReader c) =>
            {
                RlpReader fields = c.EnterSequence(out int itemLength);

                (Hash256, ulong) result = (fields.DecodeKeccak(), fields.DecodeULong());

                c.ExitSequence(fields, itemLength);
                return result;
            }, limit: RlpLimit);

            return new NewBlockHashesMessage(blockHashes);
        }
    }
}
