// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.RlpWriter;

public sealed class RlpContentLengthWriter : IRlpWriter
{
    public int ContentLength { get; private set; } = 0;

    public void WriteByte(byte value) { }

    public void Write(byte value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(int value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(ulong value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(UInt256 value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(long value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(Address value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(Memory<byte>? value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(byte[] value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(Rlp value)
    {
        ContentLength += value.Length;
    }

    public void Write(byte[]?[] value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(Hash256? value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write(bool value)
    {
        ContentLength += Rlp.LengthOf(value);
    }

    public void Write<T>(IRlpStreamDecoder<T> decoder, T value, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ContentLength += decoder.GetLength(value, rlpBehaviors);
    }

    public void WriteSequence(Action<IRlpWriter> action)
    {
        var writer = new RlpContentLengthWriter();
        action(writer);
        ContentLength += Rlp.LengthOfSequence(writer.ContentLength);
    }

    public void Wrap(bool when, int bytes, Action<IRlpWriter> action)
    {
        var writer = new RlpContentLengthWriter();
        action(writer);

        if (when)
        {
            ContentLength = Rlp.LengthOfSequence(writer.ContentLength + bytes);
        }
        else
        {
            ContentLength += writer.ContentLength + bytes;
        }
    }
}
