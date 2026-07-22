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
    public ulong Size { get; private set; }

    public bool TrySaveWord(in UInt256 location, Span<byte> word)
    {
        if (word.Length != WordSize) ThrowArgumentOutOfRangeException();

        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength, out bool outOfGas);
        if (outOfGas) return false;

        int offset = TruncateToInt32(location.u0);
        EvmWord word1 = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        UpdateSize(newLength);
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, offset), word1);
        return true;
    }

    public bool TrySaveByte(in UInt256 location, byte value)
    {
        CheckMemoryAccessViolation(in location, 1, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        int offset = TruncateToInt32(location.u0);
        UpdateSize(newLength);
        _memory![offset] = value;
        return true;
    }

    public bool TrySave(in UInt256 location, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        CheckMemoryAccessViolation(in location, (ulong)value.Length, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        UpdateSize(newLength);
        value.CopyTo(_memory.AsSpan(TruncateToInt32(location.u0), value.Length));
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

        Array.Copy(value, 0, _memory!, TruncateToInt32(location.u0), value.Length);
        return true;
    }

    /// <summary>
    /// Variant of <see cref="TrySave"/> requiring the caller to have already invoked
    /// <see cref="IGasPolicy{TSelf}.UpdateMemoryCost"/> for (<paramref name="location"/>,
    /// <paramref name="value"/>.Length) — which both bounds-checks and grows/rents the buffer —
    /// so this skips re-validation. Mirrors <see cref="CopyAfterGas"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SaveAfterGas(in UInt256 location, ReadOnlySpan<byte> value)
    {
        int length = value.Length;
        if (length == 0)
        {
            return;
        }

        Debug.Assert(location.IsUint64);
        Debug.Assert(location.u0 + (ulong)length <= Size);
        PrepareAccessAfterGas(location.u0 + (ulong)length);

        int intLocation = TruncateToInt32(location.u0);
        value.CopyTo(_memory.AsSpan(intLocation, length));
    }

    /// <summary>
    /// Copies a source range to memory and fills any bytes beyond the source with zeroes.
    /// The caller must have already charged for and expanded the destination range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopyFromZeroExtendedAfterGas(
        in UInt256 destination,
        ReadOnlySpan<byte> source,
        in UInt256 sourceOffset,
        int length)
    {
        if (length == 0)
        {
            return;
        }

        Debug.Assert(destination.IsUint64);
        Debug.Assert(destination.u0 + (ulong)length <= Size);
        PrepareAccessAfterGas(destination.u0 + (ulong)length);

        Span<byte> target = _memory.AsSpan(TruncateToInt32(destination.u0), length);
        int copiedLength = 0;
        if (sourceOffset < source.Length)
        {
            int intSourceOffset = (int)sourceOffset;
            copiedLength = Math.Min(source.Length - intSourceOffset, length);
            source.Slice(intSourceOffset, copiedLength).CopyTo(target);
        }

        if (copiedLength != length)
        {
            target[copiedLength..].Clear();
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

        data = _memory.AsMemory(TruncateToInt32(location.u0), TruncateToInt32(length.u0));
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
        if (largeSize > _memory.Length)
        {
            return default;
        }

        ClearForTracing((ulong)largeSize);
        return _memory.AsMemory((int)location, (int)length);
    }

    private void ClearForTracing(ulong size)
    {
        if (_memory is not null && size > _lastZeroedSize)
        {
            int lengthToClear = (int)(Math.Min(size, (ulong)_memory.Length) - _lastZeroedSize);
            Array.Clear(_memory, (int)_lastZeroedSize, lengthToClear);
            _lastZeroedSize += (uint)lengthToClear;
        }
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
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, offset), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreByteAfterGas(in UInt256 location, byte value)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        PrepareAccessAfterGas(location.u0 + 1);
        _memory![offset] = value;
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
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory!), offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> LoadSpanAfterGas(in UInt256 location, ulong length)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        int intLength = TruncateToInt32(length);
        PrepareAccessAfterGas(location.u0 + length);
        return _memory!.AsSpan(offset, intLength);
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
        _memory!.AsSpan(sourceOffset, intLength).CopyTo(_memory.AsSpan(destinationOffset, intLength));
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
        return new(size, _memory);
    }

    public void Dispose()
    {
        byte[]? memory = _memory;

        if (memory is not null)
        {
            _memory = null;
            ReturnDirty(memory);
        }
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
        return _memory!.AsSpan(offset, length);
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
    private const int MaxCachedArrayLength = 1 << 16;
    private const int CacheSlots = 16;
    // How far past the demanded size RentSlow zeroes, so word-at-a-time growth (e.g. MSTORE copy
    // loops) amortizes to one RentSlow per chunk instead of one per word.
    private const uint ZeroAheadSize = 4_096;

    // Arrays in the cache (and in the large pools) are DIRTY; _lastZeroedSize tracks the zeroed
    // prefix of the current buffer and RentSlow zeroes lazily as Size grows. This keeps frame
    // teardown free of the speculative full clear the old return-time scheme paid.
    [ThreadStatic] private static byte[]?[]? _cachedArrays;
    [ThreadStatic] private static int _cachedArrayCount;

    private static byte[] RentDirty(int minLength)
    {
        byte[]?[]? cache = _cachedArrays;
        int cachedArrayCount = _cachedArrayCount - 1;
        for (int i = cachedArrayCount; i >= 0; i--)
        {
            byte[] candidate = cache![i]!;
            if (candidate.Length >= minLength)
            {
                _cachedArrayCount = cachedArrayCount;
                cache[i] = cache[cachedArrayCount];
                cache[cachedArrayCount] = null;
                return candidate;
            }
        }

        if (minLength > MaxCachedArrayLength)
        {
            return RentLarge(minLength);
        }

        return new byte[BitOperations.RoundUpToPowerOf2((uint)minLength)];
    }

    private static void ReturnDirty(byte[] array)
    {
        if (array.Length > MaxCachedArrayLength)
        {
            ReturnLarge(array);
            return;
        }

        byte[]?[] cache = _cachedArrays ??= new byte[CacheSlots][];
        if (_cachedArrayCount < CacheSlots)
        {
            cache[_cachedArrayCount++] = array;
        }
    }

#if ZK_EVM
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow()
    {
        byte[]? memory = _memory;
        if (memory is null)
        {
            _memory = memory = RentDirty((int)Math.Max((uint)Size, MinRentSize));
            _lastZeroedSize = 0;
        }
        else if (Size > (ulong)memory.LongLength)
        {
            byte[] beforeResize = memory;
            _memory = memory = RentDirty(TruncateToInt32(Size));
            Array.Copy(beforeResize, 0, memory, 0, beforeResize.Length);
            ReturnDirty(beforeResize);
            // The zeroed prefix survives the copy, so _lastZeroedSize remains valid.
        }

        // Lazily guarantee [0..Size) reads as zeros; rented buffers are dirty. The first clear is
        // exact (small frames dominate); growth clears a chunk ahead so word-at-a-time expansion
        // (e.g. MSTORE copy loops) amortizes to one RentSlow per chunk.
        ulong target = _lastZeroedSize == 0 ? Size : Math.Max(Size, _lastZeroedSize + ZeroAheadSize);
        ulong zeroUpTo = Math.Min(target, (ulong)memory.Length);
        if (zeroUpTo > _lastZeroedSize)
        {
            Array.Clear(memory, (int)_lastZeroedSize, (int)(zeroUpTo - _lastZeroedSize));
            _lastZeroedSize = zeroUpTo;
        }
    }

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
