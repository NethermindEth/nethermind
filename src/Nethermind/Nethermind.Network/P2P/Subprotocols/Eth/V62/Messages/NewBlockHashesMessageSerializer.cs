// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockHashesMessageSerializer : IZeroMessageSerializer<NewBlockHashesMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<NewBlockHashesMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NewBlockHashesMessage.BlockHashes));

        public void Serialize(Span<byte> buffer, NewBlockHashesMessage message)
        {
            GetLength(message, out int contentLength);
            RlpWriter writer = new(buffer);

            writer.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                int miniContentLength = Rlp.LengthOf(message.BlockHashes[i].Item1);
                miniContentLength += Rlp.LengthOf(message.BlockHashes[i].Item2);
                writer.StartSequence(miniContentLength);
                writer.Encode(message.BlockHashes[i].Item1);
                writer.Encode(message.BlockHashes[i].Item2);
            }
        }

        public NewBlockHashesMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            NewBlockHashesMessage msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        public int GetLength(NewBlockHashesMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                int miniContentLength = Rlp.LengthOf(message.BlockHashes[i].Item1);
                miniContentLength += Rlp.LengthOf(message.BlockHashes[i].Item2);
                contentLength += Rlp.LengthOfSequence(miniContentLength);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        private static NewBlockHashesMessage Deserialize(ref RlpReader ctx)
        {
            (Hash256, ulong)[] blockHashes = ctx.DecodeNonNullArray(static (ref RlpReader c) =>
            {
                int length = c.ReadSequenceLength();
                int checkPosition = c.Position + length;

                (Hash256, ulong) result = (c.DecodeKeccak(), c.DecodeULong());

                c.Check(checkPosition);
                return result;
            }, false, limit: RlpLimit);

            return new NewBlockHashesMessage(blockHashes);
        }
    }
}
