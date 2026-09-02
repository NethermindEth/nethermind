// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Walk;

internal sealed class RowArena
{
    private const int ChunkShift = 24;
    private const int MaxChunkSize = 1 << ChunkShift;
    private const int FirstChunkSize = 64 * 1024;

    private readonly List<byte[]> _chunks = [];
    private int _position;
    private int _nextChunkSize = FirstChunkSize;

    public long Bytes { get; private set; }

    public int Append(ReadOnlySpan<byte> value)
    {
        if (value.Length > MaxChunkSize) throw new ArgumentOutOfRangeException(nameof(value), value.Length, "A history row value exceeds the arena chunk size.");

        if (_chunks.Count == 0 || _position + value.Length > _chunks[^1].Length)
        {
            _chunks.Add(new byte[Math.Max(_nextChunkSize, value.Length)]);
            _position = 0;
            _nextChunkSize = Math.Min(MaxChunkSize, _nextChunkSize * 2);
        }

        int offset = ((_chunks.Count - 1) << ChunkShift) | _position;
        value.CopyTo(_chunks[^1].AsSpan(_position));
        _position += value.Length;
        Bytes += value.Length;
        return offset;
    }

    public ReadOnlySpan<byte> Slice(int offset, int length) =>
        length == 0 ? ReadOnlySpan<byte>.Empty : _chunks[offset >> ChunkShift].AsSpan(offset & (MaxChunkSize - 1), length);
}
