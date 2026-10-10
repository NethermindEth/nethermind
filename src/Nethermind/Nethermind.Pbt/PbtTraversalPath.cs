// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

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
    public PbtTraversalPath(Span<byte> buffer)
    {
        buffer.Clear();
        _buffer = buffer;
        BitDepth = 0;
    }

    /// <summary>Copies an immutable path into a caller-owned buffer.</summary>
    public static PbtTraversalPath FromPath<TPath>(Span<byte> buffer, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath cursor = new(buffer);
        PbtNodePathOperations.CopyTo(path, buffer);
        cursor.BitDepth = path.BitDepth;
        return cursor;
    }

    public readonly ReadOnlySpan<byte> Bytes => _buffer[..PbtBitPrefix.ByteCount(BitDepth)];

    /// <summary>Gets the consumed MSB-first bit count.</summary>
    public int BitDepth { get; private set; }

    /// <summary>Appends a group boundary slot to this cursor at a group depth.</summary>
    /// <remarks>The slot can span two bytes; the second byte is written only when the slot reaches it.</remarks>
    public void AppendMut(int slot)
    {
        Debug.Assert(
            PbtThreeLevelGroupGeometry.IsGroupDepth(BitDepth) && BitDepth + PbtThreeLevelGroupGeometry.LevelsPerGroup <= _buffer.Length * 8,
            "Only a group-aligned cursor with room for a slot takes one.");
        int shifted = slot << (16 - PbtThreeLevelGroupGeometry.LevelsPerGroup - (BitDepth & 7));
        _buffer[BitDepth >> 3] |= (byte)(shifted >> 8);
        if ((byte)shifted != 0) _buffer[(BitDepth >> 3) + 1] |= (byte)shifted;
        BitDepth += PbtThreeLevelGroupGeometry.LevelsPerGroup;
    }

    /// <summary>Extends the cursor to a depth using the corresponding bits of a complete key.</summary>
    public void AppendKey(scoped ReadOnlySpan<byte> key, int depth)
    {
        PbtBitPrefix.CopyBits(key, BitDepth, depth - BitDepth, _buffer, BitDepth);
        BitDepth = depth;
    }

    /// <summary>Restores an ancestor depth and clears the removed bits.</summary>
    public void Truncate(int depth)
    {
        int byteLength = PbtBitPrefix.ByteCount(depth);
        _buffer[byteLength..PbtBitPrefix.ByteCount(BitDepth)].Clear();
        if ((depth & 7) != 0) _buffer[byteLength - 1] &= (byte)(0xFF << (8 - (depth & 7)));
        BitDepth = depth;
    }

    /// <summary>Creates an immutable snapshot of the current path.</summary>
    public readonly TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> =>
        TPath.Create(_buffer[..PbtBitPrefix.ByteCount(BitDepth)], BitDepth);
}
