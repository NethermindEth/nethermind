// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockMessageSerializer(BlockDecoder blockDecoder = null) : IMessageSerializer<NewBlockMessage>
    {
        private readonly BlockDecoder _blockDecoder = blockDecoder ?? new();

        public int Serialize(Span<byte> destination, NewBlockMessage message)
        {
            RlpWriter writer = new(destination);

            writer.StartSequence(GetContentLength(message));
            _blockDecoder.Encode(ref writer, message.Block);
            writer.Encode(message.TotalDifficulty);
            return writer.Position;
        }

        public NewBlockMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            NewBlockMessage message = Deserialize(ref ctx);
            ctx.Check(data.Length);
            return message;
        }

        public int GetLength(NewBlockMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private int GetContentLength(NewBlockMessage message) =>
            _blockDecoder.GetLength(message.Block, RlpBehaviors.None) +
            Rlp.LengthOf(message.TotalDifficulty);

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
