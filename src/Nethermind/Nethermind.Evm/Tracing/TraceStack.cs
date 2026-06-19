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

    /// <summary>Materializes the stack words as <see cref="UInt256"/> domain values, avoiding the per-word hex string allocation of a textual representation.</summary>
    public UInt256[] ToWordArray()
    {
        UInt256[] words = new UInt256[Count];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = new UInt256(this[i].Span, isBigEndian: true);
        }

        return words;
    }

    public ReadOnlySpan<byte> Peek(int index) => this[^(index + 1)].Span;
    public UInt256 PeekUInt256(int index) => new(Peek(index), true);
    public Address PeekAddress(int index) => new(Peek(index)[12..]);
}
