// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockBodiesMessageSerializer : IZeroMessageSerializer<GetBlockBodiesMessage>
    {
        private const int MaxBodyHashesPerRequest = 1024;
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetBlockBodiesMessage>(MaxBodyHashesPerRequest, nameof(GetBlockBodiesMessage.BlockHashes));

        public void Serialize(Span<byte> buffer, GetBlockBodiesMessage message)
        {
            GetLength(message, out int contentLength);
            RlpWriter writer = new(buffer);

            writer.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                writer.Encode(message.BlockHashes[i]);
            }
        }

        public GetBlockBodiesMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            GetBlockBodiesMessage msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        public int GetLength(GetBlockBodiesMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.BlockHashes[i]);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public static GetBlockBodiesMessage Deserialize(ref RlpReader ctx)
        {
            Hash256[] hashes = ctx.DecodeNonNullArray(static (ref RlpReader c) => c.DecodeKeccak(), false, limit: RlpLimit);
            return new GetBlockBodiesMessage(hashes);
        }
    }
}
