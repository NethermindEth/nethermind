// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public abstract class SnapSerializerBase<T> : IZeroMessageSerializer<T> where T : MessageBase
    {
        public abstract void Serialize(Span<byte> buffer, T message);
        protected abstract T Deserialize(ref RlpReader ctx);
        public abstract int GetLength(T message, out int contentLength);

        protected RlpWriter GetRlpWriterAndStartSequence(Span<byte> buffer, T msg)
        {
            GetLength(msg, out int contentLength);
            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);

            return writer;
        }

        public T Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            T msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }
    }
}
