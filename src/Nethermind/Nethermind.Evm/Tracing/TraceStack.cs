// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.Tracing;

public readonly struct TraceStack
{
    private readonly ReadOnlyMemory<byte> _stack;

    public TraceStack(ReadOnlyMemory<byte> stack)
    {
        _stack = stack;
    }

    public ReadOnlyMemory<byte> this[int index]
    {
        get => _stack.Slice(EvmStack.WordSize * index, EvmStack.WordSize);
    }

    public int Count => _stack.Length / EvmStack.WordSize;

    public List<string> ToHexWordList()
    {
        List<string> hexWordList = new(Count);
        for (int i = 0; i < Count; i += 1)
        {
            hexWordList.Add(this[i].Span.ToHexString(true, true));
        }

        return hexWordList;
    }
}
