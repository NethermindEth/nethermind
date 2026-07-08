// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

    public bool TrySave(in UInt256 location, Span<byte> value)
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
        value.Span.CopyTo(_memory.AsSpan(intLocation, value.Span.Length));
        if (value.PaddingLength > 0)
        {
            ClearPadding(_memory, intLocation + value.Span.Length, value.PaddingLength);
        }

        return true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ClearPadding(byte[] memory, int offset, int length)
            => memory.AsSpan(offset, length).Clear();
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
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);

        if (spanLength > 0)
        {
            value.Span.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref memory, intLocation), spanLength));
        }
        if (value.PaddingLength > 0)
        {
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref memory, intLocation + spanLength), value.PaddingLength).Clear();
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
            ReturnDirty(memory, (int)Math.Min(Size, (ulong)memory.Length));
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

#if ZK_EVM
    private const int CleanCacheSlots = 16;

    [ThreadStatic] private static byte[]?[]? _cleanArrays;
    [ThreadStatic] private static int _cleanArrayCount;

    private static byte[] RentBuffer(int minLength, out int dirtyLength)
    {
        dirtyLength = 0;
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

    private static void ReturnDirty(byte[] array, int dirtyLength)
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
#else
    // A rented buffer must be fully zero: unwritten EVM memory reads as zero. Writes are bounded
    // by Size (gas-paid), so a returned buffer's only dirty span is [0, Size) — the DirtyLength
    // the zeroer clears before republishing the buffer as clean.
    private const int MinClassExponent = 10; // 2^10 == MinRentSize
    private const int ClassCount = 7;         // 2^10 .. 2^16 (MaxCachedArrayLength)
    private const int MaxCleanPerClass = 64;
    private const int MaxDirtyBacklog = 256;
    private const int ZeroerIdleTimeoutMs = 50;

    private static readonly ConcurrentQueue<byte[]>[] _cleanBuffers = CreateQueues<byte[]>();
    private static readonly ConcurrentQueue<(byte[] Data, int DirtyLength)>[] _dirtyBuffers = CreateQueues<(byte[], int)>();
    private static readonly int[] _cleanCounts = new int[ClassCount];
    private static int _dirtyCount;
    private static readonly AutoResetEvent _zeroerSignal = new(false);
    private static int _zeroerStarted;

    private static ConcurrentQueue<T>[] CreateQueues<T>()
    {
        ConcurrentQueue<T>[] queues = new ConcurrentQueue<T>[ClassCount];
        for (int i = 0; i < ClassCount; i++)
        {
            queues[i] = new ConcurrentQueue<T>();
        }

        return queues;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClassIndex(int length) =>
        BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)length)) - MinClassExponent;

    private static byte[] RentBuffer(int minLength, out int dirtyLength)
    {
        if (minLength > MaxCachedArrayLength)
        {
            byte[] large = RentLarge(minLength);
            dirtyLength = large.Length;
            return large;
        }

        int classIndex = ClassIndex(Math.Max(minLength, MinRentSize));
        if (_cleanBuffers[classIndex].TryDequeue(out byte[]? buffer))
        {
            Interlocked.Decrement(ref _cleanCounts[classIndex]);
            dirtyLength = 0;
            return buffer;
        }

        if (_dirtyBuffers[classIndex].TryDequeue(out (byte[] Data, int DirtyLength) dirty))
        {
            Interlocked.Decrement(ref _dirtyCount);
            dirtyLength = dirty.DirtyLength;
            return dirty.Data;
        }

        dirtyLength = 0;
        return new byte[1 << (classIndex + MinClassExponent)];
    }

    private static void ReturnDirty(byte[] array, int dirtyLength)
    {
        if (array.Length > MaxCachedArrayLength)
        {
            ReturnLarge(array);
            return;
        }

        // Bounded backlog: overflow buffers are dropped (GC'd) and renters fall back to the
        // inline clear, so a burst degrades to the pre-change cost rather than growing unbounded.
        if (Interlocked.Increment(ref _dirtyCount) > MaxDirtyBacklog)
        {
            Interlocked.Decrement(ref _dirtyCount);
            return;
        }

        _dirtyBuffers[ClassIndex(array.Length)].Enqueue((array, dirtyLength));
        EnsureZeroerStarted();

        // Signal only on the idle->busy edge; the ZeroerIdleTimeoutMs poll backstops a missed
        // edge, so at worst a buffer waits one poll interval — never lost.
        if (Volatile.Read(ref _dirtyCount) == 1)
        {
            _zeroerSignal.Set();
        }
    }

    private static void EnsureZeroerStarted()
    {
        if (Volatile.Read(ref _zeroerStarted) == 0 && Interlocked.Exchange(ref _zeroerStarted, 1) == 0)
        {
            new Thread(ZeroerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "EVM Memory Zeroer",
            }.Start();
        }
    }

    private static void ZeroerLoop()
    {
        while (true)
        {
            if (!DrainDirtyBuffers())
            {
                _zeroerSignal.WaitOne(ZeroerIdleTimeoutMs);
            }
        }
    }

    // One sweep of the dirty queues into the clean pool; returns whether anything was cleared.
    // Runs on the zeroer thread; also called directly by tests to drive the clear-and-republish
    // path deterministically.
    internal static bool DrainDirtyBuffers()
    {
        bool didWork = false;
        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            while (Volatile.Read(ref _cleanCounts[classIndex]) < MaxCleanPerClass
                   && _dirtyBuffers[classIndex].TryDequeue(out (byte[] Data, int DirtyLength) dirty))
            {
                Interlocked.Decrement(ref _dirtyCount);
                Array.Clear(dirty.Data, 0, dirty.DirtyLength);
                Interlocked.Increment(ref _cleanCounts[classIndex]);
                _cleanBuffers[classIndex].Enqueue(dirty.Data);
                didWork = true;
            }
        }

        return didWork;
    }
#endif

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
        if (_memory is null)
        {
            _memory = RentBuffer((int)Math.Max((uint)Size, MinRentSize), out int dirtyLength);
            if (dirtyLength != 0) Array.Clear(_memory, 0, dirtyLength);
        }
        else if (Size > (ulong)_memory.LongLength)
        {
            byte[] beforeResize = _memory;
            _memory = RentBuffer(TruncateToInt32(Size), out int dirtyLength);
            if (dirtyLength != 0) Array.Clear(_memory, 0, dirtyLength);
            Array.Copy(beforeResize, 0, _memory, 0, beforeResize.Length);
            ReturnDirty(beforeResize, beforeResize.Length);
        }

        _lastZeroedSize = (ulong)_memory.Length;
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
