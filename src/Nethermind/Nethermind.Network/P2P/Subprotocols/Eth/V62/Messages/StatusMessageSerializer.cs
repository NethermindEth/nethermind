// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class StatusMessageSerializer : IZeroInnerMessageSerializer<StatusMessage>
    {
        private const int ForkHashLength = 5;

        public void Serialize(IByteBuffer byteBuffer, StatusMessage message) => this.SerializeWithLengths(byteBuffer, message);

        public void Serialize(IByteBuffer byteBuffer, StatusMessage message, ref LengthStack lengths)
        {
            Hash256 bestHash = GetRequiredHash(message.BestHash, nameof(message.BestHash));
            Hash256 genesisHash = GetRequiredHash(message.GenesisHash, nameof(message.GenesisHash));
            int forkIdContentLength = 0;

            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
            }

            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Pop());
            writer.Encode(message.ProtocolVersion);
            writer.Encode(message.NetworkId);
            writer.Encode(message.TotalDifficulty);
            writer.Encode(bestHash);
            writer.Encode(genesisHash);
            if (message.ForkId is not null)
            {
                ForkId forkId = message.ForkId.Value;
                writer.StartSequence(forkIdContentLength);
                Span<byte> forkHash = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(forkHash, forkId.ForkHash);
                writer.Encode(forkHash);
                writer.Encode(forkId.Next);
            }
        }

        public int PopulateLength(StatusMessage message, ref LengthStack lengths)
        {
            Hash256 bestHash = GetRequiredHash(message.BestHash, nameof(message.BestHash));
            Hash256 genesisHash = GetRequiredHash(message.GenesisHash, nameof(message.GenesisHash));
            int forkIdSequenceLength = 0;
            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                int forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
                forkIdSequenceLength = Rlp.LengthOfSequence(forkIdContentLength);
            }

            int contentLength =
                Rlp.LengthOf(message.ProtocolVersion) +
                Rlp.LengthOf(message.NetworkId) +
                Rlp.LengthOf(message.TotalDifficulty) +
                Rlp.LengthOf(bestHash) +
                Rlp.LengthOf(genesisHash) +
                forkIdSequenceLength;

            lengths.Push(contentLength);
            return Rlp.LengthOfSequence(contentLength);
        }

        public StatusMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        private static StatusMessage Deserialize(ref RlpReader ctx)
        {
            StatusMessage statusMessage = new();
            ctx.ReadSequenceLength();
            statusMessage.ProtocolVersion = ctx.DecodeByte();
            statusMessage.NetworkId = ctx.DecodeULong();
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

        private static Hash256 GetRequiredHash(Hash256? hash, string propertyName) =>
            hash ?? throw new RlpException($"{propertyName} is required in {nameof(StatusMessage)}.");
    }
}
