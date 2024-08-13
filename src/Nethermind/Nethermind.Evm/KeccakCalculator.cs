// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm;

/// <summary>
/// TODO:
/// 1. Consider creating a single thread pool work item that communicates by passing a pointer to the work item.
/// Then a ConcurrentQueue could be used.
/// 2. Alignment maybe? Why not to align to the word, especially if 1 runner is used
/// 3. Make the book keeping, so that once the work is done,
/// it's walked through and checked that it was completed so that calculator can be reused.
/// </summary>
public sealed class KeccakCalculator : IThreadPoolWorkItem
{
    private const byte HashLength = Hash256.Size;
    private const byte MinLength = HashLength;

    private const byte MaxLength = byte.MaxValue - 1;
    private const byte DoneMarker = byte.MaxValue;

    private const byte LengthPrefix = 1;
    private const int BufferLength = 8 * 1024;

    private const int NotWorking = 0;
    private const int Working = 1;

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    private struct State
    {
        private const int Size = CacheLineSize * 3;
        private const int CacheLineSize = 64;

        [FieldOffset(CacheLineSize * 1)] public ushort WrittenTo;
        [FieldOffset(CacheLineSize * 2)] public int Status;
    }

    private State _state;

    private readonly byte[] _buffer = new byte[BufferLength];

    public bool TrySchedule(ReadOnlySpan<byte> bytes, out ushort id)
    {
        var writtenTo = _state.WrittenTo;

        if (bytes.Length < MinLength || bytes.Length > MaxLength ||
            writtenTo + LengthPrefix + bytes.Length > BufferLength)
        {
            id = 0;
            return false;
        }

        // write length prefix
        id = writtenTo;

        _buffer[writtenTo] = (byte)bytes.Length;

        bytes.CopyTo(_buffer.AsSpan(writtenTo + LengthPrefix, bytes.Length));

        // Publish
        Volatile.Write(ref _state.WrittenTo, (ushort)(writtenTo + LengthPrefix + bytes.Length));

        // Ensure the work item
        if (_state.Status == NotWorking)
        {
            Volatile.Write(ref _state.Status, Working);
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
        }

        return true;
    }

    public void Copy(ushort id, Span<byte> destination)
    {
        var wait = default(SpinWait);

        while (Volatile.Read(ref _buffer[id]) != DoneMarker)
        {
            wait.SpinOnce();
        }

        _buffer.AsSpan(id + LengthPrefix, HashLength).CopyTo(destination);
    }

    public void Execute()
    {
        var wait = new SpinWait();
        var buffer = _buffer.AsSpan();
        var processedTo = 0;

        while (Volatile.Read(ref _state.Status) == Working)
        {
            // Consider reading volatile once per loop
            var writtenTo = Volatile.Read(ref _state.WrittenTo);

            if (processedTo < writtenTo)
            {
                ref var length = ref buffer[processedTo];

                Span<byte> span = buffer.Slice(processedTo + LengthPrefix, length);

                // Copy back to the span
                ValueKeccak.Compute(span).BytesAsSpan.CopyTo(span);

                // Move processed
                processedTo += LengthPrefix + length;

                // Mark as done writing done marker
                Volatile.Write(ref length, DoneMarker);
            }
            else
            {
                wait.SpinOnce();
            }
        }
    }

    public void Clear()
    {
        Volatile.Write(ref _state.Status, NotWorking);
        // TODO: fix the ending
        _buffer.AsSpan().Clear();
        _state = default;
    }
}
