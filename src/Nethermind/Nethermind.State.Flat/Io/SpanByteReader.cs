// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Io;

/// <summary>
/// Span-backed <see cref="IByteReader{TPin}"/>. Stored as a ref struct so the underlying
/// span's lifetime is tracked by the compiler — no raw pointers, no GC pinning concerns.
/// </summary>
public readonly ref struct SpanByteReader : IByteReader<NoOpPin>
{
    private readonly ReadOnlySpan<byte> _data;

    public SpanByteReader(ReadOnlySpan<byte> data) => _data = data;

    public long Length => _data.Length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset > (ulong)(_data.Length - output.Length)) return false;
        _data.Slice((int)offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(Bound bound)
    {
        if ((ulong)bound.Offset + (ulong)bound.Length > (ulong)_data.Length)
            throw new ArgumentOutOfRangeException(nameof(bound));
        return new NoOpPin(_data.Slice((int)bound.Offset, (int)bound.Length));
    }
}
