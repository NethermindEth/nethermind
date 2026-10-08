// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockMessageSerializer(BlockDecoder blockDecoder = null) : IZeroMessageSerializer<NewBlockMessage>
    {
        private readonly BlockDecoder _blockDecoder = blockDecoder ?? new();

        public void Serialize(Span<byte> buffer, NewBlockMessage message)
        {
            GetLength(message, out int contentLength);
            RlpWriter writer = new(buffer);

            writer.StartSequence(contentLength);
            _blockDecoder.Encode(ref writer, message.Block);
            writer.Encode(message.TotalDifficulty);
        }

        public NewBlockMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            NewBlockMessage msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        public int GetLength(NewBlockMessage message, out int contentLength)
        {
            contentLength = _blockDecoder.GetLength(message.Block, RlpBehaviors.None) +
                            Rlp.LengthOf(message.TotalDifficulty);

            return Rlp.LengthOfSequence(contentLength);
        }

        private NewBlockMessage Deserialize(ref RlpReader ctx)
        {
            NewBlockMessage message = new();
            ctx.ReadSequenceLength();
            message.Block = _blockDecoder.DecodeGuardNotNull(ref ctx);
            message.TotalDifficulty = ctx.DecodeUInt256();
            return message;
        }
    }
}
