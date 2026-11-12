// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public abstract class SnapSerializerBase<T> : IMessageSerializer<T> where T : SnapMessageBase
    {
        public int Serialize(Span<byte> destination, T message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(GetContentLength(message));
            Serialize(ref writer, message);
            writer.EndSequence(sequence);
            return writer.Length;
        }

        public T Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            return ctx.CheckEnd(Deserialize(ref ctx));
        }

        public int GetLength(T message) => Rlp.LengthOfSequence(GetContentLength(message));

        /// <summary>Writes the fields of <paramref name="message"/>, after the list prefix.</summary>
        protected abstract void Serialize(ref RlpWriter writer, T message);

        protected abstract T Deserialize(ref RlpReader ctx);

        /// <summary>Returns the length of the fields of <paramref name="message"/>, without the list prefix.</summary>
        protected abstract int GetContentLength(T message);
    }
}
