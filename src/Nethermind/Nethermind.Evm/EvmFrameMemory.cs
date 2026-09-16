// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>Owns the fixed inline memory tier for one pooled EVM frame.</summary>
/// <remarks>
/// Memory views obtained from this manager resolve their spans against the frame's current storage
/// after <see cref="SetBackingArray"/> changes it. A span or <see cref="MemoryHandle"/> already obtained
/// remains attached to the previous storage; later growth does not retarget a pin. Array extraction
/// deliberately fails while inline so a query cannot force a spill. Pinning while inline allocates a
/// dedicated array, and the returned array-backed handle owns the pin; any view over pooled storage
/// must not outlive the frame.
/// </remarks>
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
