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

    public void Push(int value)
    {
        _lengthWriter.Push(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Push(ulong value)
    {
        _lengthWriter.Push(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Push(ReadOnlySpan<byte> value)
    {
        _lengthWriter.Push(value);
        // TODO: This results in an unnecesary copy of the data.
        // Don't know how to workaround the fact that we're using ref types.
        var arr = value.ToArray();
        _actions.Add((stream) => stream.Encode(arr));
    }

    public void Push(UInt256 value)
    {
        _lengthWriter.Push(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Push(long value)
    {
        _lengthWriter.Push(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Push(Address value)
    {
        _lengthWriter.Push(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Push(Memory<byte>? value)
    {
        _lengthWriter.Push(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Push(byte[] value)
    {
        _lengthWriter.Push(value);
        _actions.Add((stream) => stream.Encode(value));
    }

    public void Push(Rlp value)
    {
        _lengthWriter.Push(value);
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
