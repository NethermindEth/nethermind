// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockMessageSerializer(BlockDecoder blockDecoder = null) : IMessageSerializer<NewBlockMessage>
    {
        private readonly BlockDecoder _blockDecoder = blockDecoder ?? new();

        public int Serialize(Span<byte> destination, NewBlockMessage message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);

            RlpSequence sequence = writer.StartSequence(GetContentLength(message));
            _blockDecoder.Encode(ref writer, message.Block);
            writer.Encode(message.TotalDifficulty);
            writer.EndSequence(sequence);
            return writer.Length;
        }

        public NewBlockMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            NewBlockMessage message = Deserialize(ref ctx);
            ctx.CheckEnd();
            return message;
        }

        public int GetLength(NewBlockMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private int GetContentLength(NewBlockMessage message) =>
            _blockDecoder.GetLength(message.Block, RlpBehaviors.None) +
            Rlp.LengthOf(message.TotalDifficulty);

        private NewBlockMessage Deserialize(ref RlpReader reader)
        {
            NewBlockMessage message = new();
            RlpReader ctx = reader.EnterSequence(out int itemLength);
            message.Block = _blockDecoder.DecodeGuardNotNull(ref ctx);
            message.TotalDifficulty = ctx.DecodeUInt256();
            reader.ExitSequenceIgnoringRest(ctx, itemLength);
            return message;
        }
    }
}
