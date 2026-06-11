// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>
/// One growable buffer shared by every call frame of a transaction, in the style of revm's
/// SharedMemory: a child frame's region starts where the parent's ends, so entering a frame
/// is a cursor read and leaving it is a cursor write — no per-frame pool rent, clear, or
/// return. Each <see cref="EvmPooledMemory"/> is a view over <c>[Base, Base + Size)</c>.
/// </summary>
/// <remarks>
/// Aliasing contract: spans and memories handed out by frames reference <see cref="_buffer"/>
/// directly. Frames are strictly LIFO and only the deepest frame writes, so a region can only
/// be observed after reuse if a reference outlives its frame. The two references that do —
/// call output (RETURN/REVERT data) and top-level transaction output — are copied to owned
/// arrays at the RETURN site. Calldata slices stay valid across growth because growth
/// abandons the old buffer to the GC instead of recycling it: an outstanding
/// <see cref="ReadOnlyMemory{T}"/> keeps the old array (a frozen snapshot, which is exactly
/// the calldata semantics) alive for as long as the child frame needs it.
/// </remarks>
public sealed class EvmMemoryArena
{
    private const int InitialCapacity = 4 * 1024;

    private byte[] _buffer = new byte[InitialCapacity];
    private int _top;

    public byte[] Buffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer;
    }

    /// <summary>End of the deepest live frame's region; the base for the next frame.</summary>
    public int Top
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _top;
    }

    /// <summary>Releases a frame's region; everything at and above <paramref name="frameBase"/> becomes reusable.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release(int frameBase)
    {
        Debug.Assert(frameBase <= _top, "Frame release out of LIFO order.");
        _top = frameBase;
    }

    /// <summary>
    /// Grows the deepest frame's region to end at <paramref name="newTop"/>, growing the buffer
    /// when needed. Only <paramref name="liveLength"/> leading bytes are preserved on growth —
    /// the frame's own region beyond its zeroed extent is stale by definition and the caller
    /// zeroes it right after.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int newTop, int liveLength)
    {
        if ((uint)newTop > (uint)_buffer.Length)
        {
            Grow(newTop, liveLength);
        }

        _top = newTop;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int required, int liveLength)
    {
        // The old buffer is deliberately dropped to the GC, not pooled: calldata slices taken
        // by live frames reference it and must keep reading the frozen snapshot.
        byte[] grown = new byte[checked((int)BitOperations.RoundUpToPowerOf2((uint)required))];
        Array.Copy(_buffer, grown, liveLength);
        _buffer = grown;
    }
}
