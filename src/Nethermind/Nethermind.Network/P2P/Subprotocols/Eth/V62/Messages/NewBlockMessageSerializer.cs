// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockMessageSerializer(BlockDecoder blockDecoder = null) : IZeroInnerMessageSerializer<NewBlockMessage>
    {
        private readonly BlockDecoder _blockDecoder = blockDecoder ?? new();

        public void Serialize(IByteBuffer byteBuffer, NewBlockMessage message) => this.SerializeWithLengths(byteBuffer, message);

        public void Serialize(IByteBuffer byteBuffer, NewBlockMessage message, ref LengthList lengths)
        {
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Read());
            _blockDecoder.Encode(ref writer, message.Block, ref lengths);
            writer.Encode(message.TotalDifficulty);
        }

        public NewBlockMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        public void PopulateLength(NewBlockMessage message, in LengthListWriter lengths)
        {
            using LengthListWriter sequence = lengths.BeginContainer();
            _blockDecoder.PopulateLength(message.Block, sequence);
            sequence.Add(Rlp.LengthOf(message.TotalDifficulty));
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
