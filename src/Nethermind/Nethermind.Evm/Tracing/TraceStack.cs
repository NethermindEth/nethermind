// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public readonly struct TraceStack(ReadOnlyMemory<byte> stack)
{
    private readonly ReadOnlyMemory<byte> _stack = stack;

    public ReadOnlyMemory<byte> this[int index]
    {
        get => _stack.Slice(EvmStack.WordSize * index, EvmStack.WordSize);
    }

    public int Count => _stack.Length / EvmStack.WordSize;

    public string[] ToHexWordList()
    {
        string[] hexWordList = new string[Count];
        for (int i = 0; i < hexWordList.Length; i += 1)
        {
            hexWordList[i] = this[i].Span.ToHexString(true, true);
        }

        return hexWordList;
    }

    /// <summary>Returns a copy of the raw stack bytes (one 32-byte word per slot, bottom-of-stack first).
    /// Returns an empty array for an empty stack. The EVM reuses its internal buffer across opcodes, so a copy is required.</summary>
    public byte[] ToRawBytes()
    {
        if (_stack.Length == 0) return Array.Empty<byte>();
        byte[] raw = new byte[_stack.Length];
        _stack.Span.CopyTo(raw);
        return raw;
    }

    public ReadOnlySpan<byte> Peek(int index) => this[^(index + 1)].Span;
    public UInt256 PeekUInt256(int index) => new(Peek(index), true);
    public Address PeekAddress(int index) => new(Peek(index)[12..]);
}
