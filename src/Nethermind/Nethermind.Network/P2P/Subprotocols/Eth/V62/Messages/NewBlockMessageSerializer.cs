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

        public void PopulateLength(NewBlockMessage message, ref LengthStack lengths)
        {
            int contentLength = _blockDecoder.PopulateAndPop(message.Block, ref lengths) + Rlp.LengthOf(message.TotalDifficulty);
            lengths.Push(contentLength);
            lengths.Push(Rlp.LengthOfSequence(contentLength));
        }

        public void Serialize(IByteBuffer byteBuffer, NewBlockMessage message, ref LengthStack lengths)
        {
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Pop());
            _blockDecoder.Encode(ref writer, message.Block, ref lengths);
            writer.Encode(message.TotalDifficulty);
        }

        public NewBlockMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

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
