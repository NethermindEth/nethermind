// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public abstract class SnapSerializerBase<T> : IZeroInnerMessageSerializer<T> where T : MessageBase
    {
        public abstract void Serialize(IByteBuffer byteBuffer, T message);
        protected abstract T Deserialize(RlpStream rlpStream);
        public abstract int GetLength(T message, out int contentLength);

        protected NettyRlpStream GetRlpStreamAndStartSequence(IByteBuffer byteBuffer, T msg)
        {
            int totalLength = GetLength(msg, out int contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);

            return stream;
        }

        public T Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}
