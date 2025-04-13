// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class StatusMessageSerializer : IZeroInnerMessageSerializer<StatusMessage>
    {
        private const int ForkHashLength = 5;

        private readonly bool _includeTotalDifficulty;

        public StatusMessageSerializer() : this(true) { }

        protected StatusMessageSerializer(bool includeTotalDifficulty)
        {
            _includeTotalDifficulty = includeTotalDifficulty;
        }

        public void Serialize(IByteBuffer byteBuffer, StatusMessage message)
        {
            int forkIdContentLength = 0;

            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
            }

            NettyRlpStream rlpStream = new(byteBuffer);
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.ProtocolVersion);
            rlpStream.Encode(message.NetworkId);

            if (_includeTotalDifficulty)
                rlpStream.Encode(message.TotalDifficulty);

            rlpStream.Encode(message.BestHash);
            rlpStream.Encode(message.GenesisHash);
            if (message.ForkId is not null)
            {
                ForkId forkId = message.ForkId.Value;
                rlpStream.StartSequence(forkIdContentLength);
                rlpStream.Encode(forkId.HashBytes);
                rlpStream.Encode(forkId.Next);
            }
        }

        public int GetLength(StatusMessage message, out int contentLength)
        {
            int forkIdSequenceLength = 0;
            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                int forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
                forkIdSequenceLength = Rlp.LengthOfSequence(forkIdContentLength);
            }

            contentLength =
                Rlp.LengthOf(message.ProtocolVersion) +
                Rlp.LengthOf(message.NetworkId) +
                Rlp.LengthOf(message.BestHash) +
                Rlp.LengthOf(message.GenesisHash) +
                forkIdSequenceLength;

            if (_includeTotalDifficulty)
                contentLength += Rlp.LengthOf(message.TotalDifficulty);

            return Rlp.LengthOfSequence(contentLength);
        }

        public StatusMessage Deserialize(IByteBuffer byteBuffer)
        {
            StatusMessage statusMessage = new();
            DeserializeInto(statusMessage, byteBuffer);
            return statusMessage;
        }

        protected void DeserializeInto(StatusMessage statusMessage, IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            rlpStream.ReadSequenceLength();
            statusMessage.ProtocolVersion = rlpStream.DecodeByte();
            statusMessage.NetworkId = rlpStream.DecodeUInt256();

            if (_includeTotalDifficulty)
                statusMessage.TotalDifficulty = rlpStream.DecodeUInt256();

            statusMessage.BestHash = rlpStream.DecodeKeccak();
            statusMessage.GenesisHash = rlpStream.DecodeKeccak();
            if (rlpStream.Position < rlpStream.Length)
            {
                rlpStream.ReadSequenceLength();
                uint forkHash = (uint)rlpStream.DecodeUInt256(ForkHashLength - 1);
                ulong next = rlpStream.DecodeUlong();
                ForkId forkId = new(forkHash, next);
                statusMessage.ForkId = forkId;
            }
        }
    }
}
