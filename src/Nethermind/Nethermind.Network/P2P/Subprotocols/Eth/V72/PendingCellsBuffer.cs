// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// A batch of flattened blob cells buffered until they can be validated against and
/// merged into the pooled transaction, including the source of every retained column.
/// </summary>
internal readonly struct PendingCellsBuffer
{
    public PendingCellsBuffer(BlobCellMask cellMask, byte[][] cells, PublicKey sourcePeerId)
        : this(cellMask, cells, [new PendingCellsSource(sourcePeerId, cellMask)])
    {
    }

    private PendingCellsBuffer(BlobCellMask cellMask, byte[][] cells, PendingCellsSource[] sources)
    {
        CellMask = cellMask;
        Cells = cells;
        Sources = sources;
        ByteLength = GetByteLength(cells);
    }

    public BlobCellMask CellMask { get; }
    public byte[][] Cells { get; }
    public PendingCellsSource[] Sources { get; }
    public int ByteLength { get; }
    public PublicKey SourcePeerId => Sources[^1].PeerId;

    public bool IsFromSinglePeer(PublicKey peerId)
        => Sources.Length == 1 && Sources[0].PeerId == peerId;

    public int GetByteLength(PublicKey peerId)
    {
        int cellsPerBlob = CellMask.Count;
        if (cellsPerBlob == 0 || Cells.Length % cellsPerBlob != 0)
        {
            return 0;
        }

        int blobCount = Cells.Length / cellsPerBlob;
        int byteLength = 0;
        for (int i = 0; i < Sources.Length; i++)
        {
            if (Sources[i].PeerId == peerId)
            {
                byteLength += blobCount * Sources[i].CellMask.Count * CkzgLib.Ckzg.BytesPerCell;
            }
        }

        return byteLength;
    }

    public bool TryMerge(in PendingCellsBuffer added, out PendingCellsBuffer merged)
    {
        merged = this;
        int currentCellsPerBlob = CellMask.Count;
        int addedCellsPerBlob = added.CellMask.Count;
        if (currentCellsPerBlob == 0
            || addedCellsPerBlob == 0
            || Cells.Length % currentCellsPerBlob != 0
            || added.Cells.Length % addedCellsPerBlob != 0)
        {
            return false;
        }

        int blobCount = Cells.Length / currentCellsPerBlob;
        if (added.Cells.Length / addedCellsPerBlob != blobCount)
        {
            return false;
        }

        BlobCellMask missingMask = added.CellMask.Except(CellMask);
        if (missingMask.IsEmpty)
        {
            return true;
        }

        byte[][] addedCells = missingMask == added.CellMask
            ? added.Cells
            : BlobCellsHelper.SelectFlattenedCells(added.Cells, added.CellMask, missingMask, blobCount);
        byte[][] mergedCells = BlobCellsHelper.MergeFlattenedCells(Cells, CellMask, addedCells, missingMask, blobCount);
        List<PendingCellsSource> mergedSources = new(Sources.Length + added.Sources.Length);
        for (int i = 0; i < Sources.Length; i++)
        {
            AddSource(mergedSources, Sources[i]);
        }

        for (int i = 0; i < added.Sources.Length; i++)
        {
            BlobCellMask retainedMask = added.Sources[i].CellMask & missingMask;
            if (!retainedMask.IsEmpty)
            {
                AddSource(mergedSources, new PendingCellsSource(added.Sources[i].PeerId, retainedMask));
            }
        }

        merged = new PendingCellsBuffer(CellMask | missingMask, mergedCells, mergedSources.ToArray());
        return true;
    }

    public bool TryRemoveSource(PublicKey peerId, out PendingCellsBuffer? remaining)
    {
        remaining = this;
        BlobCellMask retainedMask = BlobCellMask.Empty;
        List<PendingCellsSource>? retainedSources = null;
        bool found = false;
        for (int i = 0; i < Sources.Length; i++)
        {
            PendingCellsSource source = Sources[i];
            if (source.PeerId == peerId)
            {
                found = true;
                continue;
            }

            (retainedSources ??= new(Sources.Length - 1)).Add(source);
            retainedMask |= source.CellMask;
        }

        if (!found)
        {
            return false;
        }

        if (retainedMask.IsEmpty)
        {
            remaining = null;
            return true;
        }

        int cellsPerBlob = CellMask.Count;
        if (cellsPerBlob == 0 || Cells.Length % cellsPerBlob != 0)
        {
            remaining = null;
            return true;
        }

        int blobCount = Cells.Length / cellsPerBlob;
        byte[][] retainedCells = retainedMask == CellMask
            ? Cells
            : BlobCellsHelper.SelectFlattenedCells(Cells, CellMask, retainedMask, blobCount);
        remaining = new PendingCellsBuffer(retainedMask, retainedCells, retainedSources!.ToArray());
        return true;
    }

    private static void AddSource(List<PendingCellsSource> sources, PendingCellsSource source)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].PeerId == source.PeerId)
            {
                sources[i] = sources[i] with { CellMask = sources[i].CellMask | source.CellMask };
                return;
            }
        }

        sources.Add(source);
    }

    private static int GetByteLength(byte[][] cells)
    {
        long byteLength = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            byteLength += cells[i]?.Length ?? 0;
            if (byteLength > int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)byteLength;
    }
}

internal readonly record struct PendingCellsSource(PublicKey PeerId, BlobCellMask CellMask);
