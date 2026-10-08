// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    public class GetBlockAccessListsMessageSerializer : SnapSerializerBase<GetBlockAccessListsMessage>
    {
        public override void Serialize(Span<byte> buffer, GetBlockAccessListsMessage message)
        {
            RlpWriter writer = GetRlpWriterAndStartSequence(buffer, message);

            writer.Encode(message.RequestId);
            writer.Encode(message.BlockHashes);
            writer.Encode(message.Bytes);
        }

        protected override GetBlockAccessListsMessage Deserialize(ref RlpReader ctx)
        {
            GetBlockAccessListsMessage message = new();
            ctx.ReadSequenceLength();

            message.RequestId = ctx.DecodeLong();
            message.BlockHashes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeValueKeccakNonNull(), limit: SnapMessageLimits.GetBlockAccessListsHashesRlpLimit);
            message.Bytes = ctx.DecodeLong();

            return message;
        }

        public override int GetLength(GetBlockAccessListsMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.BlockHashes, true);
            contentLength += Rlp.LengthOf(message.Bytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
