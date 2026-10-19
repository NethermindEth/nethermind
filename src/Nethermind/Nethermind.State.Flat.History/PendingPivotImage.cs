// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>The image of a snap pivot that is being written as history rows, split into partitions by the first byte
/// of the address hash, with the set of partitions already complete.</summary>
/// <remarks>Not thread-safe: the writer changes and persists the set of done partitions under its own lock.</remarks>
public sealed class PendingPivotImage
{
    /// <summary>The number of partitions: one for each value of the first byte of the address hash.</summary>
    public const int Partitions = 256;
    internal const int BitmapBytes = Partitions / 8;

    private readonly byte[] _done;

    /// <summary>A new image with no partition done.</summary>
    /// <param name="pivotBlock">The snap-sync pivot whose state the image holds.</param>
    /// <param name="pivotStateRoot">The state root of <paramref name="pivotBlock"/>.</param>
    /// <param name="imageBlock">The block the rows are written at, at or below <paramref name="pivotBlock"/>.</param>
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

    /// <summary>The snap-sync pivot whose state the image holds.</summary>
    public ulong PivotBlock { get; }

    /// <summary>The state root of <see cref="PivotBlock"/>; the image resumes only while the persisted state is at it.</summary>
    public ValueHash256 PivotStateRoot { get; }

    /// <summary>The block the rows are written at: the start of the pivot's checkpoint window.</summary>
    public ulong ImageBlock { get; }

    /// <summary>The number of partitions already complete, from 0 to <see cref="Partitions"/>.</summary>
    public int DoneCount
    {
        get
        {
            int count = 0;
            foreach (byte bits in _done) count += BitOperations.PopCount(bits);
            return count;
        }
    }

    /// <summary>Whether all rows of <paramref name="partition"/> are written.</summary>
    /// <param name="partition">The first byte of the address hash, from 0 to <see cref="Partitions"/> - 1.</param>
    public bool IsDone(int partition) => (_done[partition >> 3] & (1 << (partition & 7))) != 0;

    /// <summary>Records <paramref name="partition"/> as complete. Call only after its rows are written, and persist the
    /// image after the rows so a restart never skips a partition whose rows are lost.</summary>
    /// <param name="partition">The first byte of the address hash, from 0 to <see cref="Partitions"/> - 1.</param>
    public void MarkDone(int partition) => _done[partition >> 3] |= (byte)(1 << (partition & 7));

    internal void CopyDoneTo(Span<byte> destination) => _done.CopyTo(destination);
}
