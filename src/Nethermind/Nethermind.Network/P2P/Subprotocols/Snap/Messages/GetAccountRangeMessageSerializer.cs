// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetAccountRangeMessageSerializer : SnapSerializerBase<GetAccountRangeMessage>
    {
        protected override GetAccountRangeMessage Deserialize(RlpStream rlpStream)
        {
            GetAccountRangeMessage message = new();
            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();
            message.AccountRange = new(rlpStream.DecodeValueKeccak(), rlpStream.DecodeValueKeccak(), rlpStream.DecodeValueKeccak());
            message.ResponseBytes = rlpStream.DecodeLong();

            return message;
        }

        public override void Serialize(IByteBuffer byteBuffer, GetAccountRangeMessage message)
        {
            NettyRlpStream rlpStream = GetRlpStreamAndStartSequence(byteBuffer, message);

            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.AccountRange.RootHash);
            rlpStream.Encode(message.AccountRange.StartingHash);

            rlpStream.Encode(message.AccountRange.LimitHash ?? Keccak.MaxValue.ValueKeccak);
            rlpStream.Encode(message.ResponseBytes == 0 ? 1000_000 : message.ResponseBytes);
        }

        public override int GetLength(GetAccountRangeMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.AccountRange.RootHash);
            contentLength += Rlp.LengthOf(message.AccountRange.StartingHash);
            contentLength += Rlp.LengthOf(message.AccountRange.LimitHash);
            contentLength += Rlp.LengthOf(message.ResponseBytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
