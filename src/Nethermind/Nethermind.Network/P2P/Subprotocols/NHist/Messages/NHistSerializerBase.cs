// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public abstract class NHistSerializerBase<T> : IZeroInnerMessageSerializer<T> where T : NHistMessageBase
{
    public abstract void Serialize(IByteBuffer byteBuffer, T message);
    protected abstract T Deserialize(ref RlpReader ctx);
    public abstract int GetLength(T message, out int contentLength);

    protected ByteBufferRlpWriter GetRlpWriterAndStartSequence(IByteBuffer byteBuffer, T msg)
    {
        int totalLength = GetLength(msg, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(contentLength);

        return writer;
    }

    public T Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);
}
