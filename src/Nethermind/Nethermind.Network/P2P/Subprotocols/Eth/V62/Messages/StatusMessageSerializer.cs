// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class StatusMessageSerializer : IZeroInnerMessageSerializer<StatusMessage>
    {
        private const int ForkHashLength = 5;

        public void Serialize(IByteBuffer byteBuffer, StatusMessage message)
        {
            Hash256 bestHash = GetRequiredHash(message.BestHash, nameof(message.BestHash));
            Hash256 genesisHash = GetRequiredHash(message.GenesisHash, nameof(message.GenesisHash));
            int forkIdContentLength = 0;

            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
            }

            int totalLength = GetLength(message, out int contentLength);
            RlpWriter writer = byteBuffer.StartRlpWrite(totalLength);
            RlpSequence sequence = writer.StartSequence(contentLength);
            writer.Encode(message.ProtocolVersion);
            writer.Encode(message.NetworkId);
            writer.Encode(message.TotalDifficulty);
            writer.Encode(bestHash);
            writer.Encode(genesisHash);
            if (message.ForkId is not null)
            {
                ForkId forkId = message.ForkId.Value;
                RlpSequence forkIdSequence = writer.StartSequence(forkIdContentLength);
                Span<byte> forkHash = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(forkHash, forkId.ForkHash);
                writer.Encode(forkHash);
                writer.Encode(forkId.Next);
                writer.EndSequence(forkIdSequence);
            }

            writer.EndSequence(sequence);
            byteBuffer.CompleteRlpWrite(in writer, totalLength);
        }

        public int GetLength(StatusMessage message, out int contentLength)
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

            contentLength =
                Rlp.LengthOf(message.ProtocolVersion) +
                Rlp.LengthOf(message.NetworkId) +
                Rlp.LengthOf(message.TotalDifficulty) +
                Rlp.LengthOf(bestHash) +
                Rlp.LengthOf(genesisHash) +
                forkIdSequenceLength;

            return Rlp.LengthOfSequence(contentLength);
        }

        public StatusMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        private static StatusMessage Deserialize(ref RlpReader reader)
        {
            StatusMessage statusMessage = new();
            RlpReader ctx = reader.EnterSequence(out int itemLength);
            statusMessage.ProtocolVersion = ctx.DecodeByte();
            statusMessage.NetworkId = ctx.DecodeULong();
            statusMessage.TotalDifficulty = ctx.DecodeUInt256();
            statusMessage.BestHash = ctx.DecodeKeccak();
            statusMessage.GenesisHash = ctx.DecodeKeccak();
            if (ctx.HasNext)
            {
                RlpReader forkIdReader = ctx.EnterSequence(out int forkIdLength);
                uint forkHash = (uint)forkIdReader.DecodeUInt256(ForkHashLength - 1);
                ulong next = forkIdReader.DecodeULong();
                ctx.ExitSequenceIgnoringRest(forkIdReader, forkIdLength);
                ForkId forkId = new(forkHash, next);
                statusMessage.ForkId = forkId;
            }

            reader.ExitSequenceIgnoringRest(ctx, itemLength);
            return statusMessage;
        }

        private static Hash256 GetRequiredHash(Hash256? hash, string propertyName) =>
            hash ?? throw new RlpReaderException($"{propertyName} is required in {nameof(StatusMessage)}.");
    }
}
