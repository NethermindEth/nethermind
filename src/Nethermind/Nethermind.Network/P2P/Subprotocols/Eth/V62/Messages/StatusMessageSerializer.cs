// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class StatusMessageSerializer : IZeroInnerMessageSerializer<StatusMessage>
    {
        private const int ForkHashLength = 5;

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
                Rlp.LengthOf(message.TotalDifficulty) +
                Rlp.LengthOf(message.BestHash) +
                Rlp.LengthOf(message.GenesisHash) +
                forkIdSequenceLength;

            return Rlp.LengthOfSequence(contentLength);
        }

        public StatusMessage Deserialize(IByteBuffer byteBuffer)
        {
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            StatusMessage message = Deserialize(ref ctx);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return message;
        }

        private static StatusMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
        {
            StatusMessage statusMessage = new();
            ctx.ReadSequenceLength();
            statusMessage.ProtocolVersion = ctx.DecodeByte();
            statusMessage.NetworkId = ctx.DecodeUInt256();
            statusMessage.TotalDifficulty = ctx.DecodeUInt256();
            statusMessage.BestHash = ctx.DecodeKeccak();
            statusMessage.GenesisHash = ctx.DecodeKeccak();
            if (ctx.Position < ctx.Length)
            {
                ctx.ReadSequenceLength();
                uint forkHash = (uint)ctx.DecodeUInt256(ForkHashLength - 1);
                ulong next = ctx.DecodeULong();
                ForkId forkId = new(forkHash, next);
                statusMessage.ForkId = forkId;
            }

            return statusMessage;
        }
    }
}
