// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;

/// <summary>
/// This class provides a capability keep some <see cref="EvmStack"/> words as a kind of promise off-heap.
///
/// When the value is asked to be materialized with <see cref="Get"/>, the first byte of the slot is checked for <see cref="DoneMarker"/>
/// If it's resolved, the value is returned. Otherwise, a <see cref="SpinWait"/> is used to spin till it is.
///
/// Once value is materialized, it can be retrieved multiple times.
///
/// The materialization is left for other components, like <see cref="KeccakCalculator"/> that preserve the semantics of having the first byte
/// different from <see cref="DoneMarker"/> untill the promise is pending.
/// </summary>
public static class OffHeapStack
{
    /// <summary>
    /// Represents the value of the first byte that should be set to it to provide the value.
    /// </summary>
    public const byte DoneMarker = byte.MaxValue;

    /// <summary>
    /// How many bytes of the prefix.
    /// </summary>
    public const byte MarkerPrefixLength = 1;

    public const int WordLength = EvmStack.WordSize;

    public static unsafe ReadOnlySpan<byte> Get(Slot slot)
    {
        Debug.Assert(slot.Value != UIntPtr.Zero);

        var wait = default(SpinWait);

        ref var start = ref Unsafe.AsRef<byte>(slot.Value.ToPointer());

        while (Volatile.Read(ref start) != DoneMarker)
        {
            wait.SpinOnce();
        }

        return new ReadOnlySpan<byte>(Unsafe.AsPointer(ref Unsafe.Add(ref start, MarkerPrefixLength)), WordLength);
    }

    /// <summary>
    /// The slot for the off heap stack.
    /// </summary>
    public record struct Slot(UIntPtr Value)
    {
        /// <summary>
        /// Whether the slot represents a promise or is just a zero.
        /// </summary>
        public bool IsAcquired => Value != UIntPtr.Zero;
    }
}

/// <summary>
/// The calculator for keccaks, allowing the caller to hold only an <see cref="ushort"/> if it keeps the reference to <see cref="KeccakCalculator"/>.
/// </summary>
public sealed class KeccakCalculator
{
    private static readonly int Alignment = UIntPtr.Size;

    private const byte MinLength = OffHeapStack.WordLength;

    private const byte MaxLength = OffHeapStack.DoneMarker - 1;

    private const byte LengthPrefix = OffHeapStack.MarkerPrefixLength;

    private const int BufferLength = 8 * 1024;

    private static readonly ConcurrentQueue<UIntPtr> Queue = new();

    public static void StartWorker(CancellationToken ct) => new Thread(start: () => RunWorker(ct)) { IsBackground = true }.Start();

    private static unsafe void RunWorker(CancellationToken ct)
    {
        while (ct.IsCancellationRequested == false)
        {
            while (Queue.TryDequeue(out var item))
            {
                byte* b = (byte*)item.ToPointer();

                var length = *b;
                var span = new Span<byte>(b + LengthPrefix, length);

                // Copy back to the span
                ValueKeccak.Compute(span).BytesAsSpan.CopyTo(span);

                // Mark as done writing done marker after writing the payload
                Volatile.Write(ref Unsafe.AsRef<byte>(b), OffHeapStack.DoneMarker);
            }

            // Spin with no sleep
            default(SpinWait).SpinOnce();
        }
    }

    private int _writtenTo;

    private readonly unsafe byte* _buffer = (byte*)NativeMemory.AlignedAlloc(BufferLength, (UIntPtr)Alignment);

    [SkipLocalsInit]
    public unsafe OffHeapStack.Slot TrySchedule(ReadOnlySpan<byte> bytes)
    {
        var writtenTo = _writtenTo;
        var length = bytes.Length;

        var alignedLength = AlignLength(length);

        if (length < MinLength || length > MaxLength ||
            writtenTo + alignedLength > BufferLength)
        {
            return default;
        }

        // Calculate start
        byte* start = _buffer + writtenTo;

        // Remember as a slot
        var slot = new OffHeapStack.Slot(new UIntPtr(start));

        // Set length
        *start = (byte)length;

        var destination = new Span<byte>(start + LengthPrefix, length);
        bytes.CopyTo(destination);

        Queue.Enqueue(slot.Value);

        // Update written to
        _writtenTo = writtenTo + alignedLength;

        return slot;
    }

    /// <summary>
    /// A quick and dirty reset to allow more writes.
    /// </summary>
    public void ResetForTests()
    {
        _writtenTo = 0;
    }

    public static ReadOnlySpan<byte> Get(OffHeapStack.Slot slot) => OffHeapStack.Get(slot);

    /// <summary>
    /// Aligns lengths so that two payloads to compute never share the same word.
    /// </summary>
    private static int AlignLength(int length)
    {
        return Align(LengthPrefix + length, Alignment);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Align(int value, int alignment)
        {
            return (value + (alignment - 1)) & ~(alignment - 1);
        }
    }
}
