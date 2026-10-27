// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public abstract class SnapSerializerBase<T> : IZeroInnerMessageSerializer<T> where T : MessageBase
    {
        public void Serialize(IByteBuffer byteBuffer, T message) => this.SerializeWithLengths(byteBuffer, message);
        public abstract void Serialize(IByteBuffer byteBuffer, T message, ref LengthStack lengths);
        protected abstract T Deserialize(ref RlpReader ctx);

        public abstract void PopulateLength(T message, ref LengthStack lengths);

        /// <summary>Pops the content length pushed under the total by <see cref="PopulateLength"/> and starts the message sequence.</summary>
        protected static ByteBufferRlpWriter GetRlpWriterAndStartSequence(IByteBuffer byteBuffer, ref LengthStack lengths)
        {
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Pop());

            return writer;
        }

        public T Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);
    }
}
