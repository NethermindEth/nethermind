// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Test.Hsst;

/// <summary>
/// Long-aware <see cref="IHsstByteReader{NoOpPin}"/> backed by a raw byte pointer
/// (typically into a memory-mapped file). Test-only — used to validate that the
/// HSST read path can navigate &gt;2 GiB HSSTs once the per-HSST builder cap is
/// lifted. PinBuffer returns a zero-copy slice; individual pins are bounded by
/// <see cref="int.MaxValue"/> by construction (a single Span&lt;byte&gt; can't
/// exceed that), but the absolute offset can be anywhere in the long-sized
/// underlying region.
/// </summary>
public readonly unsafe ref struct MmapByteReader(byte* basePtr, long size) : IHsstByteReader<NoOpPin>
{
    private readonly byte* _basePtr = basePtr;
    public long Length => size;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset + (ulong)output.Length > (ulong)Length) return false;
        new ReadOnlySpan<byte>(_basePtr + offset, output.Length).CopyTo(output);
        return true;
    }

    public bool TryReadWithReadahead(long offset, scoped Span<byte> output) => TryRead(offset, output);

    public NoOpPin PinBuffer(long offset, long size)
    {
        if ((ulong)offset + (ulong)size > (ulong)Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new NoOpPin(new ReadOnlySpan<byte>(_basePtr + offset, checked((int)size)));
    }
}
