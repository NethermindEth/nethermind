// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetStorageRangesMessageSerializer : SnapSerializerBase<GetStorageRangeMessage>
    {

        public override void Serialize(IByteBuffer byteBuffer, GetStorageRangeMessage message)
        {
            NettyRlpStream rlpStream = GetRlpStreamAndStartSequence(byteBuffer, message);

            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.StoragetRange.RootHash);
            rlpStream.Encode(message.StoragetRange.Accounts.Select(a => a.Path).ToArray()); // TODO: optimize this
            rlpStream.Encode(message.StoragetRange.StartingHash);
            rlpStream.Encode(message.StoragetRange.LimitHash);
            rlpStream.Encode(message.ResponseBytes);
        }

        protected override GetStorageRangeMessage Deserialize(RlpStream rlpStream)
        {
            GetStorageRangeMessage message = new();
            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();

            message.StoragetRange = new();
            message.StoragetRange.RootHash = rlpStream.DecodeKeccak();
            message.StoragetRange.Accounts = rlpStream.DecodeArray(DecodePathWithRlpData);
            message.StoragetRange.StartingHash = rlpStream.DecodeKeccak();
            message.StoragetRange.LimitHash = rlpStream.DecodeKeccak();
            message.ResponseBytes = rlpStream.DecodeLong();

            return message;
        }

        private PathWithAccount DecodePathWithRlpData(RlpStream stream)
        {
            return new() { Path = stream.DecodeKeccak() };
        }

        public override int GetLength(GetStorageRangeMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.StoragetRange.RootHash);
            contentLength += Rlp.LengthOf(message.StoragetRange.Accounts.Select(a => a.Path).ToArray(), true); // TODO: optimize this
            contentLength += Rlp.LengthOf(message.StoragetRange.StartingHash);
            contentLength += Rlp.LengthOf(message.StoragetRange.LimitHash);
            contentLength += Rlp.LengthOf(message.ResponseBytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
