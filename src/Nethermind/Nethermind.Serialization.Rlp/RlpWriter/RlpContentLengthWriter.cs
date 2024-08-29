// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.RlpWriter;

public sealed class RlpContentLengthWriter : IRlpWriter
{
    public int ContentLength { get; private set; } = 0;

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
}
