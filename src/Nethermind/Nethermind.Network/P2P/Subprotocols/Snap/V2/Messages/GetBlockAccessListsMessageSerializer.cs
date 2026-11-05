// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    public class GetBlockAccessListsMessageSerializer : SnapSerializerBase<GetBlockAccessListsMessage>
    {
        protected override void Serialize(ref RlpWriter writer, GetBlockAccessListsMessage message)
        {
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

        protected override int GetContentLength(GetBlockAccessListsMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.BlockHashes, true);
            contentLength += Rlp.LengthOf(message.Bytes);

            return contentLength;
        }
    }
}
