// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class StatusMessageSerializer : IZeroMessageSerializer<StatusMessage>
    {
        private const int ForkHashLength = 5;

        public void Serialize(Span<byte> buffer, StatusMessage message)
        {
            Hash256 bestHash = GetRequiredHash(message.BestHash, nameof(message.BestHash));
            Hash256 genesisHash = GetRequiredHash(message.GenesisHash, nameof(message.GenesisHash));
            int forkIdContentLength = 0;

            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
            }

            GetLength(message, out int contentLength);
            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            writer.Encode(message.ProtocolVersion);
            writer.Encode(message.NetworkId);
            writer.Encode(message.TotalDifficulty);
            writer.Encode(bestHash);
            writer.Encode(genesisHash);
            if (message.ForkId is not null)
            {
                ForkId forkId = message.ForkId.Value;
                writer.StartSequence(forkIdContentLength);
                writer.Encode(forkId.HashBytes);
                writer.Encode(forkId.Next);
            }
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

        public StatusMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            StatusMessage msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

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
