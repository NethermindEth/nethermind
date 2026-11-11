// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    // Words rather than bytes, so the memory starts on a word boundary, as the swizzled layout a guest may keep it in
    // needs (see SwizzledMemory).
    [InlineArray(EvmPooledMemory.InlineCapacity / sizeof(uint))]
    private struct InlineMemory
    {
        private uint _element0;
    }

    private InlineMemory _inlineMemory;
    private byte[]? _backingArray;

    internal byte[]? BackingArray => _backingArray;

    public override Span<byte> GetSpan()
        => _backingArray is null ? MemoryMarshal.AsBytes((Span<uint>)_inlineMemory) : _backingArray;

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
        MemoryMarshal.AsBytes((Span<uint>)_inlineMemory).CopyTo(backingArray);
        _backingArray = backingArray;
        return backingArray;
    }
}
