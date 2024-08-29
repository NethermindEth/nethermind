// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.RlpWriter;

sealed class RlpSequenceStreamWriter : IRlpWriter
{
    private readonly RlpContentLengthWriter _lengthWriter = new();
    private readonly List<Action<RlpStream>> _actions = [];

    public void Write(int value)
    {
        _lengthWriter.Write(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Write(ulong value)
    {
        _lengthWriter.Write(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        _lengthWriter.Write(value);
        // TODO: This results in an unnecesary copy of the data.
        // Don't know how to workaround the fact that we're using ref types.
        var arr = value.ToArray();
        _actions.Add((stream) => stream.Encode(arr));
    }

    public void Write(UInt256 value)
    {
        _lengthWriter.Write(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Write(long value)
    {
        _lengthWriter.Write(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Write(Address value)
    {
        _lengthWriter.Write(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Write(Memory<byte>? value)
    {
        _lengthWriter.Write(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Write(byte[] value)
    {
        _lengthWriter.Write(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Write(Rlp value)
    {
        _lengthWriter.Write(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void WriteToStream(RlpStream stream)
    {
        stream.StartSequence(_lengthWriter.ContentLength);
        foreach (var action in _actions)
        {
            action(stream);
        }
    }
}
