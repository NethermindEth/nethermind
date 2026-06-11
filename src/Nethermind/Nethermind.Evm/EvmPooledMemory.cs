// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// A call frame's EVM memory: a view over the region <c>[_base, _base + Size)</c> of the
/// transaction's <see cref="EvmMemoryArena"/>. Frame entry captures the arena cursor, frame
/// exit releases it — no per-frame buffer rent, clear, or return.
/// </summary>
public struct EvmPooledMemory
{
    public const int WordSize = 32;
    internal const ulong MaxMemorySize = int.MaxValue - WordSize + 1;
    internal const long MaxMemoryWords = (int.MaxValue - WordSize + 1L) / WordSize;

    private EvmMemoryArena? _arena;
    private int _base;
    private ulong _lastZeroedSize;

    public ulong Size { get; private set; }

    public EvmPooledMemory(EvmMemoryArena arena)
    {
        _arena = arena;
        _base = arena.Top;
    }

    public bool TrySaveWord(in UInt256 location, Span<byte> word)
    {
        if (word.Length != WordSize) ThrowArgumentOutOfRangeException();

        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength, out bool outOfGas);
        if (outOfGas) return false;

        int offset = TruncateToInt32(location.u0);
        EvmWord word1 = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        UpdateSize(newLength);
        Unsafe.WriteUnaligned(ref FrameRef(offset), word1);
        return true;
    }

    public bool TrySaveByte(in UInt256 location, byte value)
    {
        CheckMemoryAccessViolation(in location, 1, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        int offset = TruncateToInt32(location.u0);
        UpdateSize(newLength);
        FrameRef(offset) = value;
        return true;
    }

    public bool TrySave(in UInt256 location, Span<byte> value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        CheckMemoryAccessViolation(in location, (ulong)value.Length, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        UpdateSize(newLength);
        value.CopyTo(FrameSpan(TruncateToInt32(location.u0), value.Length));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckMemoryAccessViolation(in UInt256 location, in UInt256 length, out ulong newLength, out bool isViolation)
    {
        if (!length.IsUint64)
        {
            isViolation = true;
            newLength = 0;
            return;
        }

        CheckMemoryAccessViolation(in location, length.u0, out newLength, out isViolation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckMemoryAccessViolation(in UInt256 location, ulong length, out ulong newLength, out bool isViolation)
    {
        // First pass: bail if length exceeds the aligned-memory cap or the location isn't a u64.
        // Checking length first lets the compiler drop this branch entirely at call sites with a
        // constant length (MSTORE/MSTORE8/MLOAD pass 32 or 1).
        if (length > MaxMemorySize || !location.IsUint64)
        {
            isViolation = true;
            newLength = 0;
            return;
        }

        // length <= MaxMemorySize, so (MaxMemorySize - length) does not underflow. This single
        // comparison subsumes both the unsigned-overflow check and the final bounds check that the
        // original code wrote as two separate branches.
        ulong offset = location.u0;
        if (offset > MaxMemorySize - length)
        {
            isViolation = true;
            newLength = 0;
            return;
        }

        // locU0 + length <= MaxMemorySize < 2^31, no overflow possible.
        isViolation = false;
        newLength = offset + length;
    }

    public bool TrySave(in UInt256 location, byte[] value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        ulong length = (ulong)value.Length;
        CheckMemoryAccessViolation(in location, length, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        UpdateSize(newLength);

        value.CopyTo(FrameSpan(TruncateToInt32(location.u0), value.Length));
        return true;
    }

    public bool TrySave(in UInt256 location, in ZeroPaddedSpan value)
    {
        if (value.Length == 0)
        {
            // Nothing to do
            return true;
        }

        ulong length = (ulong)value.Length;
        CheckMemoryAccessViolation(in location, length, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        UpdateSize(newLength);

        int intLocation = TruncateToInt32(location.u0);
        value.Span.CopyTo(FrameSpan(intLocation, value.Span.Length));
        if (value.PaddingLength > 0)
        {
            FrameSpan(intLocation + value.Span.Length, value.PaddingLength).Clear();
        }

        return true;
    }

    public bool TryLoadSpan(scoped in UInt256 location, out Span<byte> data)
    {
        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength, out bool isViolation);
        if (isViolation)
        {
            data = default;
            return false;
        }

        data = LoadSpan(newLength, TruncateToInt32(location.u0), WordSize);
        return true;
    }

    public bool TryLoadSpan(scoped in UInt256 location, scoped in UInt256 length, out Span<byte> data)
    {
        if (length.IsZero)
        {
            data = [];
            return true;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newLength, out bool isViolation);
        if (isViolation)
        {
            data = default;
            return false;
        }

        data = LoadSpan(newLength, TruncateToInt32(location.u0), TruncateToInt32(length.u0));
        return true;
    }

    public bool TryLoad(in UInt256 location, in UInt256 length, out ReadOnlyMemory<byte> data)
    {
        if (length.IsZero)
        {
            data = default;
            return true;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newLength, out bool isViolation);
        if (isViolation)
        {
            data = default;
            return false;
        }

        UpdateSize(newLength);

        // The slice aliases the arena buffer. It stays correct for as long as callers need it:
        // a deeper frame only writes at or above its own base (this frame's current end), and
        // arena growth abandons the old buffer to the GC, so an outstanding slice keeps reading
        // the frozen snapshot — which is exactly the calldata semantics the callers rely on.
        data = _arena!.Buffer.AsMemory(_base + TruncateToInt32(location.u0), TruncateToInt32(length.u0));
        return true;
    }

    public ReadOnlyMemory<byte> Inspect(in UInt256 location, in UInt256 length)
    {
        if (length.IsZero)
        {
            return default;
        }

        if (location > int.MaxValue)
        {
            return new byte[(long)length];
        }

        if (_arena is null || location >= Size)
        {
            return default;
        }

        UInt256 largeSize = location + length;
        if (largeSize > BackedCapacity)
        {
            return default;
        }

        ClearForTracing((ulong)largeSize);
        return _arena.Buffer.AsMemory(_base + (int)location, (int)length);
    }

    /// <summary>Bytes physically available to this frame without growing the arena buffer.</summary>
    private ulong BackedCapacity => _arena is null ? 0 : (ulong)(_arena.Buffer.Length - _base);

    private void ClearForTracing(ulong size)
    {
        if (_arena is not null && size > _lastZeroedSize)
        {
            ulong clearEnd = Math.Min(size, BackedCapacity);
            int lengthToClear = (int)(clearEnd - _lastZeroedSize);
            if (lengthToClear > 0)
            {
                Array.Clear(_arena.Buffer, _base + (int)_lastZeroedSize, lengthToClear);
                // The watermark must never pass Size: bytes beyond it are zeroed here only for
                // the tracer's view and can be dirtied again by a deeper frame reusing the
                // space, so expansion has to re-zero them.
                _lastZeroedSize = Math.Min(clearEnd, Size);
            }
        }
    }

    public long CalculateMemoryCost(in UInt256 location, ulong length, out bool outOfGas)
    {
        if (length == 0)
        {
            outOfGas = false;
            return 0L;
        }

        CheckMemoryAccessViolation(in location, length, out ulong newSize, out outOfGas);
        if (outOfGas) return 0;

        return newSize > Size ? ComputeMemoryExpansionCost(newSize) : 0L;
    }

    public long CalculateMemoryCost(in UInt256 location, in UInt256 length, out bool outOfGas)
    {
        if (length.IsZero)
        {
            outOfGas = false;
            return 0L;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newSize, out outOfGas);
        if (outOfGas) return 0;

        return newSize > Size ? ComputeMemoryExpansionCost(newSize) : 0L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreWordAfterGas(in UInt256 location, ReadOnlySpan<byte> word)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        EvmWord value = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        PrepareAccessAfterGas(location.u0 + WordSize);
        Unsafe.WriteUnaligned(ref FrameRef(offset), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreByteAfterGas(in UInt256 location, byte value)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        PrepareAccessAfterGas(location.u0 + 1);
        FrameRef(offset) = value;
    }

    /// <summary>
    /// Returns a reference to the first of 32 contiguous bytes at <paramref name="location"/> in memory,
    /// expanding the buffer (and charging gas) as needed.
    /// </summary>
    /// <remarks>
    /// The returned ref aliases the arena buffer. The caller MUST consume the ref before
    /// performing any other operation that may grow the arena, otherwise the ref becomes dangling.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref byte Load32BytesAfterGas(in UInt256 location)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        PrepareAccessAfterGas(location.u0 + WordSize);
        return ref FrameRef(offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> LoadSpanAfterGas(in UInt256 location, ulong length)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        int intLength = TruncateToInt32(length);
        PrepareAccessAfterGas(location.u0 + length);
        return FrameSpan(offset, intLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopyAfterGas(in UInt256 destination, in UInt256 source, ulong length)
    {
        if (length == 0)
        {
            return;
        }

        int destinationOffset = TruncateToInt32(destination.u0);
        int sourceOffset = TruncateToInt32(source.u0);
        int intLength = TruncateToInt32(length);

        PrepareAccessAfterGas(destination.u0 + length);
        FrameSpan(sourceOffset, intLength).CopyTo(FrameSpan(destinationOffset, intLength));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long ComputeMemoryExpansionCost(ulong newSize)
    {
        // CheckMemoryAccessViolation has already capped newSize at MaxMemorySize (< 2^31), so the
        // ceiling division cannot overflow uint and the squared terms stay below 2^52. Size is
        // maintained as a word-aligned invariant by UpdateSize, so no ceiling is required there.
        Debug.Assert(newSize <= MaxMemorySize);
        Debug.Assert(Size % WordSize == 0);

        long newActiveWords = (long)((newSize + (WordSize - 1UL)) >> 5);
        long activeWords = (long)(Size >> 5);

        // Full Yellow Paper memory cost is bounded above by ~8.8e12 gas, which fits comfortably
        // in long -- so the outOfGas propagation that older revisions carried is unreachable.
        long cost = (newActiveWords - activeWords) * GasCostOf.Memory +
            ((newActiveWords * newActiveWords) >> 9) -
            ((activeWords * activeWords) >> 9);

        UpdateSize(newSize, backIfNeeded: false);

        return cost;
    }

    public TraceMemory GetTrace()
    {
        ulong size = Size;
        ClearForTracing(size);
        // Size can exceed the backed extent when expansion was charged but never accessed;
        // TraceMemory zero-pads reads past the end of the memory it is given.
        int backedLength = (int)Math.Min(size, BackedCapacity);
        return new(size, _arena is null ? default : _arena.Buffer.AsMemory(_base, backedLength));
    }

    public void Dispose()
    {
        EvmMemoryArena? arena = _arena;

        if (arena is not null)
        {
            _arena = null;
            arena.Release(_base);
        }
    }

    private void UpdateSize(ulong length, bool backIfNeeded = true)
    {
        // CheckMemoryAccessViolation has already proven length <= MaxMemorySize, so
        // (length + 31) cannot overflow. Branchless align-up replaces the original
        // "modulo + conditional" pair: one AND and one ADD, no jumps.
        if (length > Size)
        {
            Size = (length + (WordSize - 1UL)) & ~(WordSize - 1UL);
        }

        if (backIfNeeded)
        {
            EnsureBacked();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> LoadSpan(ulong newLength, int offset, int length)
    {
        UpdateSize(newLength);
        return FrameSpan(offset, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareAccessAfterGas(ulong newLength)
    {
        Debug.Assert(newLength <= Size);
        EnsureBacked();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureBacked()
    {
        if (_arena is null || Size > _lastZeroedSize)
        {
            BackSlow();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BackSlow()
    {
        EvmMemoryArena? arena = _arena;
        if (arena is null)
        {
            // Standalone use (tests, tools): the frame owns a private arena.
            _arena = arena = new EvmMemoryArena();
            _base = arena.Top;
        }

        if (Size > _lastZeroedSize)
        {
            int zeroedLength = (int)_lastZeroedSize;
            int sizeLength = TruncateToInt32(Size);
            // Everything below the zeroed extent is this frame's live data; everything between
            // it and Size is stale bytes from released frames and must read as zeros.
            arena.Advance(_base + sizeLength, liveLength: _base + zeroedLength);
            Array.Clear(arena.Buffer, _base + zeroedLength, sizeLength - zeroedLength);
            _lastZeroedSize = Size;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte FrameRef(int offset)
        => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_arena!.Buffer), _base + offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> FrameSpan(int offset, int length)
        => _arena!.Buffer.AsSpan(_base + offset, length);

    // (int)(uint)value rather than (int)value: RyuJIT emits noticeably worse codegen for a
    // direct ulong->int narrowing (treats it as a signed truncation and keeps the operation
    // on 64-bit registers, sometimes with extra moves or a movsxd). Routing through (uint)
    // lowers to a plain 32-bit register write, which on x64 implicitly zeros the upper 32
    // bits - the JIT collapses it to a single mov. The subsequent (int) reinterpret is free
    // (same bit pattern). CheckMemoryAccessViolation caps addressable memory at MaxMemorySize
    // (< 2^31), so reinterpreting the low word as signed is always safe here.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TruncateToInt32(ulong value) => (int)(uint)value;

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowArgumentOutOfRangeException()
    {
        Metrics.EvmExceptions++;
        throw new ArgumentOutOfRangeException("EvmWord size must be 32 bytes");
    }
}

public static class UInt256Extensions
{
    extension(in UInt256 value)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ToLong() => !value.IsUint64 || value.u0 > long.MaxValue ? long.MaxValue : (long)value.u0;
    }
}
