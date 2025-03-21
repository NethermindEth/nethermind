// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Json;

#nullable enable

internal sealed class BufferSegment : ReadOnlySequenceSegment<byte>
{
    private IMemoryOwner<byte>? _memoryOwner;
    private byte[]? _array;
    private BufferSegment? _next;
    private int _end;

    /// <summary>
    /// The End represents the offset into AvailableMemory where the range of "active" bytes ends. At the point when the block is leased
    /// the End is guaranteed to be equal to Start. The value of Start may be assigned anywhere between 0 and
    /// Buffer.Length, and must be equal to or less than End.
    /// </summary>
    public int End
    {
        get => _end;
        set
        {
            Debug.Assert(value <= AvailableMemory.Length);

            _end = value;
            Memory = AvailableMemory[..value];
        }
    }

    /// <summary>
    /// Reference to the next block of data when the overall "active" bytes spans multiple blocks. At the point when the block is
    /// leased Next is guaranteed to be null. Start, End, and Next are used together in order to create a linked-list of discontiguous
    /// working memory. The "active" memory is grown when bytes are copied in, End is increased, and Next is assigned. The "active"
    /// memory is shrunk when bytes are consumed, Start is increased, and blocks are returned to the pool.
    /// </summary>
    public BufferSegment? NextSegment
    {
        get => _next;
        set
        {
            Next = value;
            _next = value;
        }
    }

    public void SetOwnedMemory(IMemoryOwner<byte> memoryOwner)
    {
        _memoryOwner = memoryOwner;
        AvailableMemory = memoryOwner.Memory;
    }

    public void SetOwnedMemory(byte[] arrayPoolBuffer)
    {
        _array = arrayPoolBuffer;
        AvailableMemory = arrayPoolBuffer;
    }

    // Resets memory and internal state, should be called when removing the segment from the linked list
    public void Reset()
    {
        ResetMemory();

        Next = null;
        RunningIndex = 0;
        _next = null;
    }

    // Resets memory only, should be called when keeping the BufferSegment in the linked list and only swapping out the memory
    public void ResetMemory()
    {
        IMemoryOwner<byte>? memoryOwner = _memoryOwner;
        if (memoryOwner is not null)
        {
            _memoryOwner = null;
            memoryOwner.Dispose();
        }
        else
        {
            Debug.Assert(_array is not null);
            ArrayPool<byte>.Shared.Return(_array);
            _array = null;
        }


        Memory = default;
        _end = 0;
        AvailableMemory = default;
    }

    // Exposed for testing
    internal object? MemoryOwner => (object?)_memoryOwner ?? _array;

    public Memory<byte> AvailableMemory { get; private set; }

    public int Length => End;

    public int WritableBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AvailableMemory.Length - End;
    }

    public void SetNext(BufferSegment segment)
    {
        Debug.Assert(segment is not null);
        Debug.Assert(Next is null);

        NextSegment = segment;

        segment = this;

        while (segment.Next is not null)
        {
            Debug.Assert(segment.NextSegment is not null);
            segment.NextSegment.RunningIndex = segment.RunningIndex + segment.Length;
            segment = segment.NextSegment;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetLength(BufferSegment startSegment, int startIndex, BufferSegment endSegment, int endIndex)
    {
        return (endSegment.RunningIndex + (uint)endIndex) - (startSegment.RunningIndex + (uint)startIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetLength(long startPosition, BufferSegment endSegment, int endIndex)
    {
        return (endSegment.RunningIndex + (uint)endIndex) - startPosition;
    }
}
