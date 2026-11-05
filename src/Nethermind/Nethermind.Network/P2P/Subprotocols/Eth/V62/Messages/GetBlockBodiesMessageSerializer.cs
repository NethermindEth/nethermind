// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockBodiesMessageSerializer : IMessageSerializer<GetBlockBodiesMessage>
    {
        private const int MaxBodyHashesPerRequest = 1024;
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetBlockBodiesMessage>(MaxBodyHashesPerRequest, nameof(GetBlockBodiesMessage.BlockHashes));

        public int Serialize(Span<byte> destination, GetBlockBodiesMessage message)
        {
            RlpWriter writer = new(destination);

            writer.StartSequence(GetContentLength(message));
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                writer.Encode(message.BlockHashes[i]);
            }

            return writer.Position;
        }

        public GetBlockBodiesMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            GetBlockBodiesMessage message = Deserialize(ref ctx);
            ctx.CheckEnd();
            return message;
        }

        public int GetLength(GetBlockBodiesMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private static int GetContentLength(GetBlockBodiesMessage message)
        {
            int contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.BlockHashes[i]);
            }

            return contentLength;
        }

        private static GetBlockBodiesMessage Deserialize(ref RlpReader ctx)
        {
            Hash256[] hashes = ctx.DecodeNonNullArray(static (ref RlpReader c) => c.DecodeKeccak(), false, limit: RlpLimit);
            return new GetBlockBodiesMessage(hashes);
        }
    }
}
