// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class GetAccountRangeMessageSerializer : SnapSerializerBase<GetAccountRangeMessage>
    {
        protected override GetAccountRangeMessage Deserialize(ref RlpReader reader)
        {
            GetAccountRangeMessage message = new();
            RlpReader ctx = reader.EnterSequence(out int itemLength);

            message.RequestId = ctx.DecodeLong();
            message.AccountRange = new(ctx.DecodeKeccak(), ctx.DecodeValueKeccakNonNull(), ctx.DecodeValueKeccakNonNull());
            message.ResponseBytes = ctx.DecodeLong();

            reader.ExitSequenceIgnoringRest(ctx, itemLength);
            return message;
        }

        protected override void Serialize(ref RlpWriter writer, GetAccountRangeMessage message)
        {
            writer.Encode(message.RequestId);
            writer.Encode(message.AccountRange.RootHash);
            writer.Encode(message.AccountRange.StartingHash);

            writer.Encode(message.AccountRange.LimitHash ?? Keccak.MaxValue);
            writer.Encode(message.ResponseBytes == 0 ? 1000_000 : message.ResponseBytes);
        }

        protected override int GetContentLength(GetAccountRangeMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.AccountRange.RootHash);
            contentLength += Rlp.LengthOf(message.AccountRange.StartingHash);
            contentLength += Rlp.LengthOf(message.AccountRange.LimitHash ?? Keccak.MaxValue);
            contentLength += Rlp.LengthOf(message.ResponseBytes == 0 ? 1000_000 : message.ResponseBytes);

            return contentLength;
        }
    }
}
