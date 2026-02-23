// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
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
            rlpStream.Encode(message.StorageRange.RootHash);
            var accounts = message.StorageRange.Accounts;
            int accountsCount = accounts.Count;
            int accountsPathsContentLength = accountsCount * Rlp.LengthOfKeccakRlp;
            rlpStream.StartSequence(accountsPathsContentLength);
            for (int i = 0; i < accountsCount; i++)
            {
                rlpStream.Encode(accounts[i].Path);
            }
            rlpStream.Encode(message.StorageRange.StartingHash);
            rlpStream.Encode(message.StorageRange.LimitHash);
            rlpStream.Encode(message.ResponseBytes);
        }

        protected override GetStorageRangeMessage Deserialize(RlpStream rlpStream)
        {
            GetStorageRangeMessage message = new();
            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();

            message.StorageRange = new();
            message.StorageRange.RootHash = rlpStream.DecodeKeccak();
            message.StorageRange.Accounts = rlpStream.DecodeArrayPoolList(DecodePathWithRlpData);
            message.StorageRange.StartingHash = rlpStream.DecodeKeccak();
            message.StorageRange.LimitHash = rlpStream.DecodeKeccak();
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
            contentLength += Rlp.LengthOf(message.StorageRange.RootHash);
            int accountsCount = message.StorageRange.Accounts.Count;
            int accountsPathsContentLength = accountsCount * Rlp.LengthOfKeccakRlp;
            contentLength += Rlp.LengthOfSequence(accountsPathsContentLength);
            contentLength += Rlp.LengthOf(message.StorageRange.StartingHash);
            contentLength += Rlp.LengthOf(message.StorageRange.LimitHash);
            contentLength += Rlp.LengthOf(message.ResponseBytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
