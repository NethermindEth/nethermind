// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class GetAccountRangeMessageSerializer : SnapSerializerBase<GetAccountRangeMessage>
    {
        protected override GetAccountRangeMessage Deserialize(ref RlpReader ctx)
        {
            GetAccountRangeMessage message = new();
            ctx.ReadSequenceLength();

            message.RequestId = ctx.DecodeLong();
            message.AccountRange = new(ctx.DecodeKeccak(), ctx.DecodeValueKeccakNonNull(), ctx.DecodeValueKeccakNonNull());
            message.ResponseBytes = ctx.DecodeLong();

            return message;
        }

        public override void Serialize(IByteBuffer byteBuffer, GetAccountRangeMessage message, ref LengthList lengths)
        {
            ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, ref lengths);

            writer.Encode(message.RequestId);
            writer.Encode(message.AccountRange.RootHash);
            writer.Encode(message.AccountRange.StartingHash);

            writer.Encode(message.AccountRange.LimitHash ?? Keccak.MaxValue);
            writer.Encode(message.ResponseBytes == 0 ? 1000_000 : message.ResponseBytes);
        }

        public override void PopulateLength(GetAccountRangeMessage message, in LengthListWriter lengths)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.AccountRange.RootHash);
            contentLength += Rlp.LengthOf(message.AccountRange.StartingHash);
            contentLength += Rlp.LengthOf(message.AccountRange.LimitHash ?? Keccak.MaxValue);
            contentLength += Rlp.LengthOf(message.ResponseBytes == 0 ? 1000_000 : message.ResponseBytes);

            using LengthListWriter sequence = lengths.BeginContainer();
            sequence.Add(contentLength);
        }
    }
}
