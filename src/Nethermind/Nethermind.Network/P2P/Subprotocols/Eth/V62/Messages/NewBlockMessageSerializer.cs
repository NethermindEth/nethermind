// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockMessageSerializer(BlockDecoder blockDecoder = null) : IZeroInnerMessageSerializer<NewBlockMessage>
    {
        private readonly BlockDecoder _blockDecoder = blockDecoder ?? new();

        public void Serialize(IByteBuffer byteBuffer, NewBlockMessage message)
        {
            int length = GetLength(message, out int contentLength);
            RlpWriter writer = byteBuffer.StartRlpWrite(length);

            RlpSequence sequence = writer.StartSequence(contentLength);
            _blockDecoder.Encode(ref writer, message.Block);
            writer.Encode(message.TotalDifficulty);

            writer.EndSequence(sequence);
            byteBuffer.CompleteRlpWrite(in writer, length);
        }

        public NewBlockMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        public int GetLength(NewBlockMessage message, out int contentLength)
        {
            contentLength = _blockDecoder.GetLength(message.Block, RlpBehaviors.None) +
                            Rlp.LengthOf(message.TotalDifficulty);

            return Rlp.LengthOfSequence(contentLength);
        }

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
