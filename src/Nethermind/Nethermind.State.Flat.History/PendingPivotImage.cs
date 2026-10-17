// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>The image of a snap pivot that is being written as history rows, split into partitions by the first byte
/// of the address hash, with the set of partitions already complete.</summary>
public sealed class PendingPivotImage
{
    public const int Partitions = 256;
    internal const int BitmapBytes = Partitions / 8;

    private readonly BitArray _done;

    public PendingPivotImage(ulong pivotBlock, in ValueHash256 pivotStateRoot, ulong imageBlock)
        : this(pivotBlock, pivotStateRoot, imageBlock, new byte[BitmapBytes])
    {
    }

    internal PendingPivotImage(ulong pivotBlock, in ValueHash256 pivotStateRoot, ulong imageBlock, ReadOnlySpan<byte> done)
    {
        PivotBlock = pivotBlock;
        PivotStateRoot = pivotStateRoot;
        ImageBlock = imageBlock;
        _done = new BitArray(done.ToArray());
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
            for (int partition = 0; partition < Partitions; partition++)
            {
                if (_done[partition]) count++;
            }

            return count;
        }
    }

    public bool IsDone(int partition) => _done[partition];

    public void MarkDone(int partition) => _done[partition] = true;

    internal void CopyDoneTo(Span<byte> destination)
    {
        byte[] bytes = new byte[BitmapBytes];
        _done.CopyTo(bytes, 0);
        bytes.CopyTo(destination);
    }
}
