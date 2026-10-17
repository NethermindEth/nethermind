// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>The image of a snap pivot that is being written as history rows, split into partitions by the first byte
/// of the address hash, with the set of partitions already complete.</summary>
public sealed class PendingPivotImage
{
    public const int Partitions = 256;
    internal const int BitmapBytes = Partitions / 8;

    private readonly byte[] _done;

    public PendingPivotImage(ulong pivotBlock, in ValueHash256 pivotStateRoot, ulong imageBlock)
        : this(pivotBlock, pivotStateRoot, imageBlock, new byte[BitmapBytes])
    {
    }

    internal PendingPivotImage(ulong pivotBlock, in ValueHash256 pivotStateRoot, ulong imageBlock, ReadOnlySpan<byte> done)
        : this(pivotBlock, pivotStateRoot, imageBlock, done.ToArray())
    {
    }

    private PendingPivotImage(ulong pivotBlock, in ValueHash256 pivotStateRoot, ulong imageBlock, byte[] done)
    {
        PivotBlock = pivotBlock;
        PivotStateRoot = pivotStateRoot;
        ImageBlock = imageBlock;
        _done = done;
    }

    public ulong PivotBlock { get; }
    public ValueHash256 PivotStateRoot { get; }

    /// <summary>The block the rows are written at: the start of the pivot's checkpoint window.</summary>
    public ulong ImageBlock { get; }

    public int DoneCount
    {
        get
        {
            int count = 0;
            foreach (byte bits in _done) count += BitOperations.PopCount(bits);
            return count;
        }
    }

    public bool IsDone(int partition) => (_done[partition >> 3] & (1 << (partition & 7))) != 0;

    public void MarkDone(int partition) => _done[partition >> 3] |= (byte)(1 << (partition & 7));

    internal void CopyDoneTo(Span<byte> destination) => _done.CopyTo(destination);
}
