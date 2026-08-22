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

        UpdateSize(newLength, rentIfNeeded: false);
        SaveAfterGas(in location, value);
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
        => TrySave(in location, value.AsSpan());

    /// <summary>
    /// Variant of <see cref="TrySave"/> requiring the caller to have already invoked
    /// <see cref="IGasPolicy{TSelf}.UpdateMemoryCost"/> for (<paramref name="location"/>,
    /// <paramref name="value"/>.Length), which bounds-checks and updates the logical memory size,
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
        int intLocation = TruncateToInt32(location.u0);
        ulong preparedZeroedSize = PrepareOverwriteAfterGas(location.u0, (ulong)length);
        value.CopyTo(_memory.AsSpan(intLocation, length));
        CommitOverwrite(preparedZeroedSize);
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
        ulong preparedZeroedSize = PrepareOverwriteAfterGas(destination.u0, (ulong)length);
        Span<byte> target = _memory.AsSpan(TruncateToInt32(destination.u0), length);
        int copiedLength = 0;
        if (sourceOffset < source.Length)
        {
            int intSourceOffset = TruncateToInt32(sourceOffset.u0);
            copiedLength = Math.Min(source.Length - intSourceOffset, length);
            source.Slice(intSourceOffset, copiedLength).CopyTo(target);
        }

        if (copiedLength != length)
        {
            target[copiedLength..].Clear();
        }

        CommitOverwrite(preparedZeroedSize);
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
        PrepareAccessAfterGas(location.u0 + WordSize);
        EvmWord value = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, offset), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreByteAfterGas(in UInt256 location, byte value)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        PrepareAccessAfterGas(location.u0 + 1);
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);
        Unsafe.Add(ref memory, offset) = value;
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

        Debug.Assert(destination.IsUint64);
        Debug.Assert(source.IsUint64);
        Debug.Assert(destination.u0 + length <= Size);
        Debug.Assert(source.u0 + length <= Size);
        int intLength = TruncateToInt32(length);
        ulong sourceAvailable = source.u0 < _lastZeroedSize
            ? Math.Min(length, _lastZeroedSize - source.u0)
            : 0;
        ulong preparedZeroedSize = PrepareOverwriteAfterGas(destination.u0, length);
        Span<byte> target = _memory.AsSpan(TruncateToInt32(destination.u0), intLength);

        if (sourceAvailable != 0)
        {
            _memory.AsSpan(TruncateToInt32(source.u0), TruncateToInt32(sourceAvailable)).CopyTo(target);
        }

        if (sourceAvailable != length)
        {
            target[TruncateToInt32(sourceAvailable)..].Clear();
        }

        CommitOverwrite(preparedZeroedSize);
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
        Size = newActiveWords << 5;

        // Full Yellow Paper memory cost is bounded above by ~8.8e12 gas, which fits comfortably
        // in ulong -- so the outOfGas propagation that older revisions carried is unreachable.
        // newActiveWords >= activeWords by the caller's gating condition, so the subtractions are safe.
        ulong cost = (newActiveWords - activeWords) * GasCostOf.Memory +
            ((newActiveWords * newActiveWords) >> 9) -
            ((activeWords * activeWords) >> 9);

        return cost;
    }

    private static readonly TraceMemory EmptyTraceMemory = new(0, default);

    public TraceMemory GetTrace()
    {
        ulong size = Size;
        if (size == 0)
            return EmptyTraceMemory;

        ClearForTracing(size);
        // Clamp to Size so TraceMemory.Slice past the EVM high-water cannot see dirty tail bytes.
        if (_memory is null)
            return new(size, default);

        int visible = (int)Math.Min(size, (ulong)_memory.Length);
        return new(size, _memory.AsMemory(0, visible));
    }

    public void Dispose()
    {
        byte[]? memory = _memory;

        if (memory is not null)
        {
            _memory = null;
            Return(memory);
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
    private ulong PrepareOverwriteAfterGas(ulong offset, ulong length)
    {
        Debug.Assert(length != 0);
        Debug.Assert(offset + length <= Size);

        byte[]? memory = _memory;
        return memory is null || Size > (ulong)memory.Length || Size > _lastZeroedSize
            ? RentForOverwriteSlow(offset, offset + length)
            : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitOverwrite(ulong preparedZeroedSize)
    {
        if (preparedZeroedSize != 0)
        {
            _lastZeroedSize = preparedZeroedSize;
        }
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
    // Above this, a cache miss rents from the shared pool instead of allocating (pow2 sizes from
    // here up are LOH-sized).
    private const int MaxNewAllocLength = 1 << 16;
    // Buffers up to this stay in the per-thread cache. Frames zero-extend their buffer on growth
    // (RentSlow), and a buffer that round-tripped through the shared pool between frames comes
    // back cold and coherence-invalidated under concurrent load — so those zeroing stores stall.
    // Keeping mid-size buffers on the renting thread keeps the lines warm; the byte budget bounds
    // per-thread retention.
    private const int MaxThreadCachedArrayLength = 1 << 18;
    private const int MaxThreadCachedBytes = 1 << 21;
    private const int CacheSlots = 16;

    [ThreadStatic] private static byte[]?[]? _cachedArrays;
    [ThreadStatic] private static int _cachedArrayCount;
    [ThreadStatic] private static int _cachedArrayBytes;

    // Explicit allocations are runtime-zeroed, while cached and pooled arrays are untrusted.
    // The initialized extent lets the slow paths preserve that distinction through growth.
    private static byte[] Rent(int minLength, out ulong initializedExtent)
    {
        byte[]?[]? cache = _cachedArrays;
        int cachedArrayCount = _cachedArrayCount - 1;
        for (int i = cachedArrayCount; i >= 0; i--)
        {
            byte[] candidate = cache![i]!;
            if (candidate.Length >= minLength)
            {
                initializedExtent = 0;
                _cachedArrayCount = cachedArrayCount;
                _cachedArrayBytes -= candidate.Length;
                cache[i] = cache[cachedArrayCount];
                cache[cachedArrayCount] = null;
                return candidate;
            }
        }

        if (minLength > MaxNewAllocLength)
        {
            return RentLarge(minLength, out initializedExtent);
        }

        byte[] fresh = new byte[BitOperations.RoundUpToPowerOf2((uint)minLength)];
        initializedExtent = (ulong)fresh.Length;
        return fresh;
    }

    private static void Return(byte[] array)
    {
        if (array.Length <= MaxThreadCachedArrayLength
            && _cachedArrayCount < CacheSlots
            && _cachedArrayBytes + array.Length <= MaxThreadCachedBytes)
        {
            byte[]?[] cache = _cachedArrays ??= new byte[CacheSlots][];
            cache[_cachedArrayCount++] = array;
            _cachedArrayBytes += array.Length;
            return;
        }

        // Provenance: arrays <= MaxNewAllocLength are plain allocations, larger ones came from
        // RentLarge — an array must never reach a pool it was not rented from, so sub-threshold
        // arrays that miss the thread cache are dropped for the GC rather than pooled.
        if (array.Length > MaxNewAllocLength)
        {
            ReturnLarge(array);
        }
    }

#if ZK_EVM
    private static byte[] RentLarge(int minLength, out ulong initializedExtent)
    {
        byte[] array = SafeArrayPool<byte>.Rent(minLength, out bool isFresh);
        initializedExtent = isFresh ? (ulong)array.Length : 0;
        return array;
    }

    private static void ReturnLarge(byte[] array) => SafeArrayPool<byte>.Shared.Return(array);
#else
    private const int MaxSharedArrayLength = 1 << 20;
    // Buffers above this limit are allocated directly and are never returned to a pool.
    private const int MaxLargePooledArrayLength = 1 << 22;
    private static readonly System.Buffers.ArrayPool<byte> _largeArrayPool =
        System.Buffers.ArrayPool<byte>.Create(maxArrayLength: MaxLargePooledArrayLength, maxArraysPerBucket: 16);

    private static byte[] RentLarge(int minLength, out ulong initializedExtent)
    {
        if (minLength > MaxLargePooledArrayLength)
        {
            byte[] fresh = new byte[minLength];
            initializedExtent = (ulong)fresh.Length;
            return fresh;
        }

        initializedExtent = 0;
        return minLength > MaxSharedArrayLength
            ? _largeArrayPool.Rent(minLength)
            : SafeArrayPool<byte>.Shared.Rent(minLength);
    }

    private static void ReturnLarge(byte[] array)
    {
        if (array.Length > MaxLargePooledArrayLength)
        {
            return;
        }

        if (array.Length > MaxSharedArrayLength)
            _largeArrayPool.Return(array);
        else
            SafeArrayPool<byte>.Shared.Return(array);
    }
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow()
    {
        ulong zeroedSize = _lastZeroedSize;
        byte[] memory = EnsureCapacity(ref zeroedSize);

        ulong size = Size;
        if (size > zeroedSize)
        {
            // Over-zero to a chunk boundary so sequential MSTORE growth does not take RentSlow per word.
            const ulong zeroChunk = 4 * 1024;
            ulong target = Math.Min((ulong)memory.Length, (size + (zeroChunk - 1)) & ~(zeroChunk - 1));
            Array.Clear(memory, (int)zeroedSize, (int)(target - zeroedSize));
            zeroedSize = target;
        }

        _lastZeroedSize = zeroedSize;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong RentForOverwriteSlow(ulong overwriteStart, ulong overwriteEnd)
    {
        ulong zeroedSize = _lastZeroedSize;
        byte[] memory = EnsureCapacity(ref zeroedSize);

        const ulong zeroChunk = 4 * 1024;
        ulong target = Math.Max(
            zeroedSize,
            Math.Min((ulong)memory.Length, (Size + (zeroChunk - 1)) & ~(zeroChunk - 1)));
        ulong beforeOverwriteEnd = Math.Min(overwriteStart, target);
        if (beforeOverwriteEnd > zeroedSize)
        {
            Array.Clear(memory, (int)zeroedSize, (int)(beforeOverwriteEnd - zeroedSize));
        }

        ulong afterOverwriteStart = Math.Max(overwriteEnd, zeroedSize);
        if (target > afterOverwriteStart)
        {
            Array.Clear(memory, (int)afterOverwriteStart, (int)(target - afterOverwriteStart));
        }

        return target;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] EnsureCapacity(ref ulong initializedSize)
    {
        byte[]? memory = _memory;
        if (memory is null)
        {
            _memory = memory = Rent((int)Math.Max((uint)Size, MinRentSize), out ulong initializedExtent);
            initializedSize = initializedExtent;
        }
        else if (Size > (ulong)memory.LongLength)
        {
            byte[] grown = Rent(TruncateToInt32(Size), out ulong initializedExtent);
            Array.Copy(memory, 0, grown, 0, (int)initializedSize);
            Return(memory);
            _memory = memory = grown;
            initializedSize = Math.Max(initializedSize, initializedExtent);
        }

        return memory;
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
