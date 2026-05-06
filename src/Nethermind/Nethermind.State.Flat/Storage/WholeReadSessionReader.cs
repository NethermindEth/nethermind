// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// <see cref="IHsstByteReader{TPin}"/> over a <see cref="WholeReadSession"/>'s mmap view.
/// Currently span-backed — behaviour identical to <see cref="SpanByteReader"/> — but kept as
/// a distinct type so the address space (a single <see cref="ArenaReservation"/>'s view) can
/// later evolve to a chunked / long-sized backing without touching call sites.
/// </summary>
public readonly ref struct WholeReadSessionReader : IHsstByteReader<NoOpPin>
{
    private readonly ReadOnlySpan<byte> _data;

    public WholeReadSessionReader(ReadOnlySpan<byte> data) => _data = data;

    public long Length => _data.Length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset > (ulong)(_data.Length - output.Length)) return false;
        _data.Slice((int)offset, output.Length).CopyTo(output);
        return true;
    }

    public bool TryReadWithReadahead(long offset, scoped Span<byte> output) => TryRead(offset, output);

    public NoOpPin PinBuffer(long offset, long size)
    {
        if ((ulong)offset + (ulong)size > (ulong)_data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new NoOpPin(_data.Slice((int)offset, (int)size));
    }
}
