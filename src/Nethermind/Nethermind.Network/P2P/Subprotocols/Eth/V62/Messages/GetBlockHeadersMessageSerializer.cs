// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockHeadersMessageSerializer : IZeroInnerMessageSerializer<GetBlockHeadersMessage>
    {
        private static readonly RlpLimit StartBlockRlpLimit = RlpLimit.For<GetBlockHeadersMessage>(Hash256.Size, nameof(GetBlockHeadersMessage.StartBlockHash));

        public static GetBlockHeadersMessage Deserialize(ref RlpReader ctx)
        {
            GetBlockHeadersMessage message = new();
            ctx.ReadSequenceLength();
            ReadOnlySpan<byte> startingBytes = ctx.DecodeByteArraySpan(StartBlockRlpLimit);
            if (startingBytes.Length == Hash256.Size)
            {
                message.StartBlockHash = new Hash256(startingBytes);
            }
            else
            {
                message.StartBlockNumber = (ulong)new UInt256(startingBytes, true);
            }

            message.MaxHeaders = ctx.DecodeUInt();
            message.Skip = ctx.DecodeUInt();
            message.Reverse = ctx.DecodeByte();
            return message;
        }

        public void Serialize(IByteBuffer byteBuffer, GetBlockHeadersMessage message) => this.SerializeWithLengths(byteBuffer, message);

        public void Serialize(IByteBuffer byteBuffer, GetBlockHeadersMessage message, ref LengthList lengths)
        {
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Read());
            if (message.StartBlockHash is null)
            {
                writer.Encode(message.StartBlockNumber);
            }
            else
            {
                writer.Encode(message.StartBlockHash);
            }

            writer.Encode(message.MaxHeaders);
            writer.Encode(message.Skip);
            writer.Encode(message.Reverse);
        }

        public GetBlockHeadersMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        public void PopulateLength(GetBlockHeadersMessage message, in LengthListWriter lengths)
        {
            using LengthListWriter sequence = lengths.BeginContainer();
            sequence.Add(message.StartBlockHash is null
                ? Rlp.LengthOf(message.StartBlockNumber)
                : Rlp.LengthOf(message.StartBlockHash));
            sequence.Add(Rlp.LengthOf(message.MaxHeaders) + Rlp.LengthOf(message.Skip) + Rlp.LengthOf(message.Reverse));
        }
    }
}
