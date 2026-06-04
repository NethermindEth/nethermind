// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.Intrinsics.X86;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// <see cref="IHsstByteReader{TPin}"/> over a <see cref="WholeReadSession"/>'s mmap view.
/// Holds a raw <c>byte*</c> + <see cref="long"/> length (pointer arithmetic on the long
/// offset, then constructs an int-sized <see cref="ReadOnlySpan{T}"/> for each pin), so
/// it correctly addresses &gt;2 GiB views without trying to materialise a single
/// <see cref="ReadOnlySpan{T}"/> over the whole reservation. The pointer's lifetime is
/// owned by the <see cref="WholeReadSession"/>; the reader assumes the session is alive.
/// </summary>
public readonly unsafe ref struct WholeReadSessionReader(byte* basePtr, long length) : IHsstByteReader<NoOpPin>
{
    private readonly byte* _basePtr = basePtr;
    public long Length => length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset + (ulong)output.Length > (ulong)length) return false;
        new ReadOnlySpan<byte>(_basePtr + offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(long offset, long size)
    {
        if ((ulong)offset + (ulong)size > (ulong)length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new NoOpPin(new ReadOnlySpan<byte>(_basePtr + offset, checked((int)size)));
    }

    /// <summary>
    /// Prefetches the body of a BTree node whose first byte was just read (page + TLB now resident):
    /// pulls the two cache lines after the header line so the floor-search's key scan finds them warm.
    /// <paramref name="offset"/> is the node start; line 0 is already cached from the flag-byte read.
    /// </summary>
    public void Prefetch(long offset)
    {
        if (!Sse.IsSupported || (ulong)offset >= (ulong)length) return;
        byte* p = _basePtr + offset;
        Sse.Prefetch0(p + 64);
        Sse.Prefetch0(p + 128);
    }
}
