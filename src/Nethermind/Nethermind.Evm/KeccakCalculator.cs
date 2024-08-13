// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm;

/// <summary>
/// The calculator for keccaks, allowing the caller to hold only an <see cref="ushort"/> if it keeps the reference to <see cref="KeccakCalculator"/>.
/// </summary>
public sealed class KeccakCalculator
{
    private static readonly int Alignment = UIntPtr.Size;

    private const byte HashLength = Hash256.Size;
    private const byte MinLength = HashLength;

    private const byte MaxLength = byte.MaxValue - 1;
    private const byte DoneMarker = byte.MaxValue;

    private const ushort IdDiff = 1;
    private const byte LengthPrefix = 1;
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
                Volatile.Write(ref Unsafe.AsRef<byte>(b), DoneMarker);
            }

            // Spin with no sleep
            default(SpinWait).SpinOnce();
        }
    }

    private ushort _writtenTo;

    private readonly unsafe byte* _buffer = (byte*)NativeMemory.AlignedAlloc(BufferLength, (UIntPtr)Alignment);

    public unsafe bool TrySchedule(ReadOnlySpan<byte> bytes, out ushort id)
    {
        var writtenTo = _writtenTo;
        var length = bytes.Length;

        var alignedLength = AlignLength(length);

        if (length < MinLength || length > MaxLength ||
            writtenTo + alignedLength > BufferLength)
        {
            id = 0;
            return false;
        }

        // write length prefix
        id = (ushort)(writtenTo + IdDiff);

        ref var start = ref Unsafe.Add(ref Unsafe.AsRef<byte>(_buffer), writtenTo);
        start = (byte)length;

        var destination = new Span<byte>(Unsafe.AsPointer(ref Unsafe.Add(ref start, LengthPrefix)), length);
        bytes.CopyTo(destination);

        Queue.Enqueue(new UIntPtr(Unsafe.AsPointer(ref start)));

        // Update written to
        _writtenTo = (ushort)(writtenTo + alignedLength);

        Debug.Assert(id > 0);

        return true;
    }

    public unsafe ReadOnlySpan<byte> Get(ushort id)
    {
        Debug.Assert(id > 0);

        var wait = default(SpinWait);

        ref var start = ref Unsafe.Add(ref Unsafe.AsRef<byte>(_buffer), id - IdDiff);

        while (Volatile.Read(ref start) != DoneMarker)
        {
            wait.SpinOnce();
        }

        return new ReadOnlySpan<byte>(Unsafe.AsPointer(ref Unsafe.Add(ref start, LengthPrefix)), HashLength);
    }

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
