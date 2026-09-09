// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

/// <summary>A tracer's view of the EVM stack, bottom-of-stack first.</summary>
/// <remarks>
/// <paramref name="slots"/> holds the words in <see cref="EvmStack"/>'s limb layout; a word is
/// reversed into <paramref name="bigEndianWords"/> when it is read, so a tracer that reads a few
/// operands pays for those rather than for the whole stack.
/// <para>
/// Both buffers belong to the executing VM and are reused by the next instruction, so anything kept
/// beyond the callback has to be copied - <see cref="ToRawBytes"/> does.
/// </para>
/// </remarks>
public readonly struct TraceStack(ReadOnlyMemory<byte> slots, Memory<byte> bigEndianWords)
{
    private readonly ReadOnlyMemory<byte> _slots = slots;
    private readonly Memory<byte> _bigEndianWords = bigEndianWords;

    public ReadOnlyMemory<byte> this[int index]
    {
        get
        {
            Memory<byte> word = _bigEndianWords.Slice(EvmStack.WordSize * index, EvmStack.WordSize);
            EvmStack.WriteBigEndianWord(_slots.Span.Slice(EvmStack.WordSize * index), word.Span);
            return word;
        }
    }

    public int Count => _slots.Length / EvmStack.WordSize;

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
        if (_slots.Length == 0) return Array.Empty<byte>();
        byte[] raw = new byte[_slots.Length];
        EvmStack.WriteBigEndianWords(_slots.Span, raw);
        return raw;
    }

    public ReadOnlySpan<byte> Peek(int index) => this[^(index + 1)].Span;

    /// <remarks>Read straight out of the slot, which already holds the <see cref="UInt256"/> layout.</remarks>
    public UInt256 PeekUInt256(int index)
    {
        ReadOnlySpan<byte> slot = _slots.Span.Slice(EvmStack.WordSize * (Count - 1 - index), EvmStack.WordSize);
        return EvmStack.ReadUInt256FromSlot(ref MemoryMarshal.GetReference(slot));
    }

    public Address PeekAddress(int index) => new(Peek(index)[12..]);
}
