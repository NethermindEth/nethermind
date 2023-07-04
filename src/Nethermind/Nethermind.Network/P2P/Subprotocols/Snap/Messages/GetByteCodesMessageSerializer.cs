// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetByteCodesMessageSerializer : SnapSerializerBase<GetByteCodesMessage>
    {
        public override void Serialize(IByteBuffer byteBuffer, GetByteCodesMessage message)
        {
            NettyRlpStream rlpStream = GetRlpStreamAndStartSequence(byteBuffer, message);

            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.Hashes);
            rlpStream.Encode(message.Bytes);
        }

        protected override GetByteCodesMessage Deserialize(RlpStream rlpStream)
        {
            GetByteCodesMessage message = new();
            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();
            message.Hashes = rlpStream.DecodeArray(_ => rlpStream.DecodeValueKeccak(out var keccak) ? keccak : default);
            message.Bytes = rlpStream.DecodeLong();

            return message;
        }

        public override int GetLength(GetByteCodesMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.Hashes, true);
            contentLength += Rlp.LengthOf(message.Bytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
