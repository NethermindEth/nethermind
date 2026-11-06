// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockHeadersMessageSerializer : IMessageSerializer<GetBlockHeadersMessage>
    {
        private static readonly RlpLimit StartBlockRlpLimit = RlpLimit.For<GetBlockHeadersMessage>(Hash256.Size, nameof(GetBlockHeadersMessage.StartBlockHash));

        public static GetBlockHeadersMessage Deserialize(ref RlpReader reader)
        {
            GetBlockHeadersMessage message = new();
            RlpReader ctx = reader.EnterSequence(out int itemLength);
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
            reader.ExitSequenceIgnoringRest(ctx, itemLength);
            return message;
        }

        public int Serialize(Span<byte> destination, GetBlockHeadersMessage message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);

            RlpSequence sequence = writer.StartSequence(GetContentLength(message));
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
            writer.EndSequence(sequence);
            return writer.Length;
        }

        public GetBlockHeadersMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            GetBlockHeadersMessage message = Deserialize(ref ctx);
            ctx.CheckEnd();
            return message;
        }

        public int GetLength(GetBlockHeadersMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private static int GetContentLength(GetBlockHeadersMessage message)
        {
            int contentLength = message.StartBlockHash is null
                ? Rlp.LengthOf(message.StartBlockNumber)
                : Rlp.LengthOf(message.StartBlockHash);
            contentLength += Rlp.LengthOf(message.MaxHeaders);
            contentLength += Rlp.LengthOf(message.Skip);
            contentLength += Rlp.LengthOf(message.Reverse);

            return contentLength;
        }
    }
}
