// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>A mutable traversal cursor backed by an operation-local buffer.</summary>
/// <remarks>
/// The caller owns the buffer and must not mutate it independently or use copies of this cursor
/// concurrently. Copies share the buffer, not the depth. Borrowers must snapshot any identity they
/// retain beyond a call; <see cref="ToPath{TPath}"/> returns an independent immutable path.
/// </remarks>
public ref struct PbtTraversalPath
{
    private readonly Span<byte> _buffer;

    /// <summary>Creates an empty cursor and clears its backing buffer.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The buffer exceeds the maximum storage key length.</exception>
    public PbtTraversalPath(Span<byte> buffer)
    {
        if (buffer.Length > PbtStorageFullKey.MaxLength) throw new ArgumentOutOfRangeException(nameof(buffer));
        buffer.Clear();
        _buffer = buffer;
        BitDepth = 0;
    }

    /// <summary>Copies an immutable path into a caller-owned buffer.</summary>
    public static PbtTraversalPath FromPath<TPath>(Span<byte> buffer, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        if ((uint)path.BitDepth > (uint)(buffer.Length * 8)) throw new ArgumentOutOfRangeException(nameof(path));
        PbtTraversalPath cursor = new(buffer);
        path.CopyBitsTo(0, buffer, 0, path.BitDepth);
        cursor.BitDepth = path.BitDepth;
        return cursor;
    }

    internal readonly ReadOnlySpan<byte> Bytes => _buffer[..PbtBitPrefix.ByteCount(BitDepth)];

    /// <summary>Gets the consumed MSB-first bit count.</summary>
    public int BitDepth { get; private set; }

    /// <summary>Appends a four-bit nibble to this cursor.</summary>
    public void AppendMut(int nibble)
    {
        if ((uint)nibble > 15) throw new ArgumentOutOfRangeException(nameof(nibble));
        int depth = BitDepth + 4;
        if (depth > _buffer.Length * 8) throw new ArgumentOutOfRangeException(nameof(nibble));
        int byteIndex = BitDepth >> 3;
        int shiftedBits = nibble << (12 - (BitDepth & 7));
        _buffer[byteIndex] |= (byte)(shiftedBits >> 8);
        if ((BitDepth & 7) > 4) _buffer[byteIndex + 1] = (byte)shiftedBits;
        BitDepth = depth;
    }

    /// <summary>Extends the cursor to a depth using the corresponding bits of a complete key.</summary>
    public void AppendKey(scoped ReadOnlySpan<byte> key, int depth)
    {
        if (depth < BitDepth || depth > _buffer.Length * 8 || depth > (long)key.Length * 8)
            throw new ArgumentOutOfRangeException(nameof(depth));
        PbtBitPrefix.CopyBits(key, BitDepth, depth - BitDepth, _buffer, BitDepth);
        BitDepth = depth;
    }

    /// <summary>Restores an ancestor depth and clears the removed bits.</summary>
    public void Truncate(int depth)
    {
        if ((uint)depth > (uint)BitDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        int byteLength = PbtBitPrefix.ByteCount(depth);
        _buffer[byteLength..PbtBitPrefix.ByteCount(BitDepth)].Clear();
        if ((depth & 7) != 0) _buffer[byteLength - 1] &= (byte)(0xFF << (8 - (depth & 7)));
        BitDepth = depth;
    }

    /// <summary>Creates an immutable snapshot of the current path.</summary>
    public readonly TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> =>
        TPath.Create(_buffer[..PbtBitPrefix.ByteCount(BitDepth)], BitDepth);
}
