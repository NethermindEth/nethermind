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

    public void Push(int value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Push(ulong value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Push(ReadOnlySpan<byte> value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Push(UInt256 value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Push(long value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Push(Address value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Push(Memory<byte>? value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Push(byte[] value)
    {
        _contentLength += Rlp.LengthOf(value);
    }

    public void Push(Rlp value)
    {
        _contentLength += value.Length;
    }
}
