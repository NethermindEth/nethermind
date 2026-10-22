// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
