// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.Intrinsics.X86;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// <see cref="IByteReader{TPin}"/> over a <see cref="WholeReadSession"/>'s mmap view.
/// Uses <c>byte*</c> + <see cref="long"/> length to correctly address &gt;2 GiB views;
/// each <see cref="PinBuffer"/> call constructs an int-sized <see cref="ReadOnlySpan{T}"/>
/// at the requested offset rather than spanning the whole reservation.
/// </summary>
/// <remarks>The pointer lifetime is owned by the <see cref="WholeReadSession"/>; the session must remain alive for the duration of any use of this reader.</remarks>
public readonly unsafe ref struct WholeReadSessionReader(byte* basePtr, long length) : IByteReader<NoOpPin>
{
    private readonly byte* _basePtr = basePtr;
    public long Length => length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset + (ulong)output.Length > (ulong)length) return false;
        new ReadOnlySpan<byte>(_basePtr + offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(Bound bound)
    {
        if ((ulong)bound.Offset + (ulong)bound.Length > (ulong)length)
            throw new ArgumentOutOfRangeException(nameof(bound));
        return new NoOpPin(new ReadOnlySpan<byte>(_basePtr + bound.Offset, checked((int)bound.Length)));
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
