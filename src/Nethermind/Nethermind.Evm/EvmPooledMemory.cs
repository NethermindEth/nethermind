// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

public struct EvmPooledMemory
{
    public const int WordSize = 32;
    internal const ulong MaxMemorySize = int.MaxValue - WordSize + 1;
    internal const long MaxMemoryWords = (int.MaxValue - WordSize + 1L) / WordSize;

    private ulong _lastZeroedSize;

    private byte[]? _memory;
    // Base index into _memory where this frame's window starts. Always zero for the ZK arena path and for
    // an unattached or spilled private buffer; equal to the shared-buffer base otherwise.
    private int _offset;
    public ulong Size { get; private set; }

#if !ZK_EVM
    // Per-VirtualMachine shared buffer this frame draws from (null when unattached, e.g. in unit tests).
    private SharedEvmMemory? _shared;
    // This frame's base offset inside the shared buffer; anchors child frames' windows (see FrameFrontier).
    private int _base;
    // Set once the frame outgrew the fixed reserve and fell back to a private pooled array.
    private bool _spilled;

    /// <summary>
    /// Binds this (freshly reset) frame's memory to the VirtualMachine's shared buffer at
    /// <paramref name="baseOffset"/>. The backing buffer is only touched lazily on first growth.
    /// </summary>
    internal void AttachShared(SharedEvmMemory shared, int baseOffset)
    {
        _shared = shared;
        _base = baseOffset;
        _offset = baseOffset;
    }

    /// <summary>
    /// Absolute offset in the shared buffer where a child frame's window should start. A spilled frame
    /// occupies no shared space, so its children reuse its base.
    /// </summary>
    internal int FrameFrontier => _spilled ? _base : _base + (int)Size;
#endif

    // The frame's memory lives at [_offset, _offset + Size) inside _memory. These helpers apply the base
    // in one place so no access can forget it (a missed offset would be a consensus fault), while keeping
    // the single-add, bounds-check-free codegen the hot MSTORE/MLOAD paths rely on.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ref byte FrameRef(int offset)
        => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory!), _offset + offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly Span<byte> FrameSpan(int offset, int length) => _memory.AsSpan(_offset + offset, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly Memory<byte> FrameMemory(int offset, int length) => _memory.AsMemory(_offset + offset, length);

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

        value.AsSpan().CopyTo(FrameSpan(TruncateToInt32(location.u0), value.Length));
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

    /// <summary>
    /// Variant of <see cref="TrySave"/> requiring the caller to have already invoked
    /// <see cref="IGasPolicy{TSelf}.UpdateMemoryCost"/> for (<paramref name="location"/>,
    /// <paramref name="value"/>.Length) — which both bounds-checks and grows/rents the buffer —
    /// so this skips re-validation. Mirrors <see cref="CopyAfterGas"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SaveAfterGas(in UInt256 location, in ZeroPaddedSpan value)
    {
        if (value.Length == 0)
        {
            return;
        }

        Debug.Assert(location.IsUint64);
        Debug.Assert(location.u0 + (ulong)value.Length <= Size);
        PrepareAccessAfterGas(location.u0 + (ulong)value.Length);

        int intLocation = TruncateToInt32(location.u0);
        int spanLength = value.Span.Length;

        if (spanLength > 0)
        {
            value.Span.CopyTo(MemoryMarshal.CreateSpan(ref FrameRef(intLocation), spanLength));
        }
        if (value.PaddingLength > 0)
        {
            MemoryMarshal.CreateSpan(ref FrameRef(intLocation + spanLength), value.PaddingLength).Clear();
        }
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

        data = FrameMemory(TruncateToInt32(location.u0), TruncateToInt32(length.u0));
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

        if (_memory is null)
        {
            return default;
        }

        if (location >= Size)
        {
            return default;
        }
        UInt256 largeSize = location + length;
        if (largeSize > (ulong)(_memory.Length - _offset))
        {
            return default;
        }

        ClearForTracing((ulong)largeSize);
        return FrameMemory((int)location, (int)length);
    }

    private void ClearForTracing(ulong size)
    {
        if (_memory is null || size <= _lastZeroedSize)
        {
            return;
        }

        int frameOld = (int)_lastZeroedSize;
        int frameNew = (int)Math.Min(size, (ulong)(_memory.Length - _offset));
        if (frameNew <= frameOld)
        {
            return;
        }
#if !ZK_EVM
        // In the shared buffer, [frameOld, frameNew) may still hold a sibling frame's bytes; clear only
        // the dirtied part via the shared zeroer so the pristine tail is left untouched.
        if (_shared is not null && !_spilled)
        {
            _shared.Zero(_base, frameOld, frameNew);
        }
        else
        {
            Array.Clear(_memory, _offset + frameOld, frameNew - frameOld);
        }
#else
        Array.Clear(_memory, _offset + frameOld, frameNew - frameOld);
#endif
        _lastZeroedSize = (ulong)frameNew;
    }

    public ulong CalculateMemoryCost(in UInt256 location, ulong length, out bool outOfGas)
    {
        if (length == 0)
        {
            outOfGas = false;
            return 0;
        }

        CheckMemoryAccessViolation(in location, length, out ulong newSize, out outOfGas);
        if (outOfGas) return 0;

        return newSize > Size ? ComputeMemoryExpansionCost(newSize) : 0;
    }

    public ulong CalculateMemoryCost(in UInt256 location, in UInt256 length, out bool outOfGas)
    {
        if (length.IsZero)
        {
            outOfGas = false;
            return 0;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newSize, out outOfGas);
        if (outOfGas) return 0;

        return newSize > Size ? ComputeMemoryExpansionCost(newSize) : 0;
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
    /// The returned ref aliases the internal memory buffer. The caller MUST consume the ref before
    /// performing any other operation that may re-rent or grow the underlying storage, otherwise the
    /// ref becomes dangling.
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
    private ulong ComputeMemoryExpansionCost(ulong newSize)
    {
        // CheckMemoryAccessViolation has already capped newSize at MaxMemorySize (< 2^31), so the
        // ceiling division cannot overflow uint and the squared terms stay below 2^52. Size is
        // maintained as a word-aligned invariant by UpdateSize, so no ceiling is required there.
        Debug.Assert(newSize <= MaxMemorySize);
        Debug.Assert(Size % WordSize == 0);

        ulong newActiveWords = (newSize + (WordSize - 1UL)) >> 5;
        ulong activeWords = Size >> 5;

        // Full Yellow Paper memory cost is bounded above by ~8.8e12 gas, which fits comfortably
        // in ulong -- so the outOfGas propagation that older revisions carried is unreachable.
        // newActiveWords >= activeWords by the gating condition in UpdateSize, so the subtractions are safe.
        ulong cost = (newActiveWords - activeWords) * GasCostOf.Memory +
            ((newActiveWords * newActiveWords) >> 9) -
            ((activeWords * activeWords) >> 9);

        UpdateSize(newSize, rentIfNeeded: false);

        return cost;
    }

    public TraceMemory GetTrace()
    {
        ulong size = Size;
        ClearForTracing(size);
        if (_memory is null)
        {
            return new(size, default);
        }

        int len = (int)Math.Min(size, (ulong)(_memory.Length - _offset));
        return new(size, FrameMemory(0, len));
    }

    public void Dispose()
    {
        byte[]? memory = _memory;
        if (memory is null)
        {
            return;
        }

        _memory = null;
#if ZK_EVM
        ReturnClean(memory, (int)Math.Min(Size, (ulong)memory.Length));
#else
        // A private (unattached or spilled) buffer is pooled here; the shared buffer belongs to the
        // VirtualMachine and is never returned — its window is simply reused by the next sibling frame,
        // whose growth re-zeroes any stale bytes on demand (see SharedEvmMemory). No per-frame clear.
        if (_shared is null || _spilled)
        {
            ReturnLarge(memory);
        }
#endif
    }

    private void UpdateSize(ulong length, bool rentIfNeeded = true)
    {
        // CheckMemoryAccessViolation has already proven length <= MaxMemorySize, so
        // (length + 31) cannot overflow. Branchless align-up replaces the original
        // "modulo + conditional" pair: one AND and one ADD, no jumps.
        if (length > Size)
        {
            Size = (length + (WordSize - 1UL)) & ~(WordSize - 1UL);
        }

        if (rentIfNeeded)
        {
            EnsureRented();
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
        EnsureRented();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureRented()
    {
        byte[]? memory = _memory;
        if (memory is null || Size > (ulong)memory.Length || Size > _lastZeroedSize)
        {
            RentSlow();
        }
    }

    private const int MinRentSize = 1_024;

#if ZK_EVM
    private const int MaxCachedArrayLength = 1 << 16;
    private const int CleanCacheSlots = 16;

    [ThreadStatic] private static byte[]?[]? _cleanArrays;
    [ThreadStatic] private static int _cleanArrayCount;

    private static byte[] RentClean(int minLength)
    {
        byte[]?[]? cache = _cleanArrays;
        int cleanArrayCount = _cleanArrayCount - 1;
        for (int i = cleanArrayCount; i >= 0; i--)
        {
            byte[] candidate = cache![i]!;
            if (candidate.Length >= minLength)
            {
                _cleanArrayCount = cleanArrayCount;
                cache[i] = cache[cleanArrayCount];
                cache[cleanArrayCount] = null;
                return candidate;
            }
        }

        if (minLength > MaxCachedArrayLength)
        {
            byte[] pooled = RentLarge(minLength);
            Array.Clear(pooled);
            return pooled;
        }

        return new byte[BitOperations.RoundUpToPowerOf2((uint)minLength)];
    }

    private static void ReturnClean(byte[] array, int dirtyLength)
    {
        if (array.Length > MaxCachedArrayLength)
        {
            ReturnLarge(array);
            return;
        }

        byte[]?[] cache = _cleanArrays ??= new byte[CleanCacheSlots][];
        if (_cleanArrayCount < CleanCacheSlots)
        {
            Array.Clear(array, 0, dirtyLength);
            cache[_cleanArrayCount++] = array;
        }
    }

    private static byte[] RentLarge(int minLength) => SafeArrayPool<byte>.Shared.Rent(minLength);

    private static void ReturnLarge(byte[] array) => SafeArrayPool<byte>.Shared.Return(array);
#else
    private const int MaxSharedArrayLength = 1 << 20;
    // Above this, buffers fall back to plain allocation (not pooled), as before this change.
    private const int MaxLargePooledArrayLength = 1 << 22;
    private static readonly System.Buffers.ArrayPool<byte> _largeArrayPool =
        System.Buffers.ArrayPool<byte>.Create(maxArrayLength: MaxLargePooledArrayLength, maxArraysPerBucket: 16);

    private static byte[] RentLarge(int minLength)
        => minLength > MaxSharedArrayLength
            ? _largeArrayPool.Rent(minLength)
            : SafeArrayPool<byte>.Shared.Rent(minLength);

    private static void ReturnLarge(byte[] array)
    {
        if (array.Length > MaxSharedArrayLength)
            _largeArrayPool.Return(array);
        else
            SafeArrayPool<byte>.Shared.Return(array);
    }
#endif

#if ZK_EVM
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow()
    {
        if (_memory is null)
        {
            _memory = RentClean((int)Math.Max((uint)Size, MinRentSize));
        }
        else if (Size > (ulong)_memory.LongLength)
        {
            byte[] beforeResize = _memory;
            _memory = RentClean(TruncateToInt32(Size));
            Array.Copy(beforeResize, 0, _memory, 0, beforeResize.Length);
            ReturnClean(beforeResize, beforeResize.Length);
        }

        _offset = 0; // the ZK arena buffer is per-frame, so the window always starts at index 0
        _lastZeroedSize = (ulong)_memory.Length;
    }
#else
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow()
    {
        // Unattached (unit tests) or already spilled → grow a private pooled array by realloc-and-copy.
        if (_shared is null || _spilled)
        {
            RentPrivate();
            return;
        }

        byte[] buffer = _shared.Buffer;
        // The frame window is [_base, _base + Size); spill to a private array if it can't fit the reserve.
        if ((ulong)_base + Size > (ulong)buffer.Length)
        {
            SpillToPrivate();
            return;
        }

        _memory = buffer; // _offset == _base, set in AttachShared
        _shared.Zero(_base, (int)_lastZeroedSize, (int)Size);
        _lastZeroedSize = Size;
    }

    // Grows a private (unattached or spilled) buffer, keeping it fully zero-initialised.
    private void RentPrivate()
    {
        if (_memory is null)
        {
            _memory = RentLargeCleared((int)Math.Max((uint)Size, MinRentSize));
        }
        else if (Size > (ulong)_memory.LongLength)
        {
            byte[] beforeResize = _memory;
            _memory = RentLargeCleared(TruncateToInt32(Size));
            Array.Copy(beforeResize, 0, _memory, 0, beforeResize.Length);
            ReturnLarge(beforeResize);
        }

        _offset = 0;
        _lastZeroedSize = (ulong)_memory.Length;
    }

    // The frame outgrew the fixed reserve: move it to a private pooled array, preserving the bytes it has
    // already written in the shared window. Rare — needs adversarially deep or large per-thread memory.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SpillToPrivate()
    {
        byte[] priv = RentLargeCleared(TruncateToInt32(Size));
        int copyLen = (int)Math.Min(_lastZeroedSize, Size);
        if (_memory is not null && copyLen > 0)
        {
            Array.Copy(_memory, _base, priv, 0, copyLen);
        }

        _memory = priv;
        _offset = 0;
        _spilled = true;
        _lastZeroedSize = (ulong)priv.Length;
    }

    private static byte[] RentLargeCleared(int minLength)
    {
        byte[] array = RentLarge(minLength);
        Array.Clear(array);
        return array;
    }
#endif

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
