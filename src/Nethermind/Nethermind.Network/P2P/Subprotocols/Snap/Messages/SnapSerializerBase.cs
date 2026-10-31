// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public abstract class SnapSerializerBase<T> : IZeroInnerMessageSerializer<T> where T : MessageBase
    {
        public abstract void Serialize(IByteBuffer byteBuffer, T message);
        protected abstract T Deserialize(ref RlpReader ctx);
        public abstract int GetLength(T message, out int contentLength);

        protected RlpWriter GetRlpWriterAndStartSequence(IByteBuffer byteBuffer, T msg, out RlpSequence sequence, out int totalLength)
        {
            totalLength = GetLength(msg, out int contentLength);
            RlpWriter writer = byteBuffer.StartRlpWrite(totalLength);
            sequence = writer.StartSequence(contentLength);

            return writer;
        }

        protected static void EndSequenceAndCompleteWrite(IByteBuffer byteBuffer, ref RlpWriter writer, RlpSequence sequence, int totalLength)
        {
            writer.EndSequence(sequence);
            byteBuffer.CompleteRlpWrite(in writer, totalLength);
        }

        public T Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);
    }
}
