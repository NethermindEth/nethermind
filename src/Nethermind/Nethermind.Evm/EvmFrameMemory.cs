// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

internal sealed class EvmFrameMemory : MemoryManager<byte>
{
    [InlineArray(EvmPooledMemory.InlineCapacity)]
    private struct InlineMemory
    {
        private byte _element0;
    }

    private InlineMemory _inlineMemory;
    private byte[]? _backingArray;

    internal byte[]? BackingArray => _backingArray;

    public override Span<byte> GetSpan()
        => _backingArray is null ? _inlineMemory : _backingArray;

    public override MemoryHandle Pin(int elementIndex = 0)
        => (_backingArray ?? Spill()).AsMemory(elementIndex).Pin();

    public override void Unpin() { }

    protected override bool TryGetArray(out ArraySegment<byte> segment)
    {
        byte[]? backingArray = _backingArray;
        if (backingArray is null)
        {
            segment = default;
            return false;
        }

        segment = backingArray;
        return true;
    }

    protected override void Dispose(bool disposing) { }

    internal void SetBackingArray(byte[]? backingArray) => _backingArray = backingArray;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] Spill()
    {
        byte[] backingArray = new byte[EvmPooledMemory.InlineCapacity];
        Span<byte> inlineMemory = _inlineMemory;
        inlineMemory.CopyTo(backingArray);
        _backingArray = backingArray;
        return backingArray;
    }
}
