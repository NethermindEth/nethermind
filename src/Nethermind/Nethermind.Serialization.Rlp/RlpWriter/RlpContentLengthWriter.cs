// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.RlpWriter;

sealed class RlpContentLengthWriter : IRlpWriter
{
    private int _contentLength = 0;

    public int ContentLength => _contentLength;

    public void Write(int value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Write(ulong value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Write(UInt256 value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Write(long value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Write(Address value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Write(Memory<byte>? value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Write(byte[] value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Write(Rlp value)
    {
        _contentLength += value.Length;
    }
}
