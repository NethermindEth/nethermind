// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Crypto;

public static class BlobCellsHelper
{
    public static bool ValidateCells(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Version is not ProofVersion.V1)
        {
            return wrapper.Cells is null && wrapper.CellMask.IsEmpty;
        }

        if (wrapper.Cells is null)
        {
            return wrapper.CellMask.IsEmpty;
        }

        if (wrapper.CellMask.IsEmpty)
        {
            return wrapper.Cells.Length == 0;
        }

        int blobCount = wrapper.Commitments.Length;
        int cellsPerBlob = wrapper.CellMask.Count;
        if ((wrapper.Blobs.Length != 0 && wrapper.Blobs.Length != blobCount)
            || wrapper.Proofs.Length != blobCount * Ckzg.CellsPerExtBlob
            || wrapper.Cells.Length != blobCount * cellsPerBlob)
        {
            return false;
        }

        for (int i = 0; i < blobCount; i++)
        {
            if (wrapper.Commitments[i].Length != Ckzg.BytesPerCommitment)
            {
                return false;
            }
        }

        for (int i = 0; i < wrapper.Proofs.Length; i++)
        {
            if (wrapper.Proofs[i].Length != Ckzg.BytesPerProof)
            {
                return false;
            }
        }

        for (int i = 0; i < wrapper.Cells.Length; i++)
        {
            if (wrapper.Cells[i].Length != Ckzg.BytesPerCell)
            {
                return false;
            }
        }

        using ArrayPoolSpan<byte> flatCommitments = new(blobCount * cellsPerBlob * Ckzg.BytesPerCommitment);
        using ArrayPoolSpan<byte> flatProofs = new(blobCount * cellsPerBlob * Ckzg.BytesPerProof);
        using ArrayPoolSpan<byte> flatCells = new(blobCount * cellsPerBlob * Ckzg.BytesPerCell);
        using ArrayPoolSpan<ulong> indices = new(blobCount * cellsPerBlob);

        try
        {
            int position = 0;
            foreach (int cellIndex in wrapper.CellMask.EnumerateSetBits())
            {
                for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
                {
                    int flatIndex = blobIndex * cellsPerBlob + position;
                    wrapper.Commitments[blobIndex].CopyTo(flatCommitments.Slice(flatIndex * Ckzg.BytesPerCommitment, Ckzg.BytesPerCommitment));
                    wrapper.Proofs[blobIndex * Ckzg.CellsPerExtBlob + cellIndex].CopyTo(flatProofs.Slice(flatIndex * Ckzg.BytesPerProof, Ckzg.BytesPerProof));
                    wrapper.Cells[flatIndex].CopyTo(flatCells.Slice(flatIndex * Ckzg.BytesPerCell, Ckzg.BytesPerCell));
                    indices[flatIndex] = (ulong)cellIndex;
                }

                position++;
            }

            return Ckzg.VerifyCellKzgProofBatch(flatCommitments, indices, flatCells, flatProofs, blobCount * cellsPerBlob, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }

    public static bool TryGetFlattenedCells(ShardBlobNetworkWrapper wrapper, BlobCellMask requestedMask, out byte[][] cells)
    {
        int blobCount = wrapper.Commitments.Length;
        BlobCellMask availableMask = wrapper.GetAvailableCellMask() & requestedMask;
        int cellsPerBlob = availableMask.Count;

        if (cellsPerBlob == 0)
        {
            cells = [];
            return false;
        }

        if (wrapper.HasFullBlobs())
        {
            cells = new byte[blobCount * cellsPerBlob][];
            using ArrayPoolSpan<byte> allCells = new(Ckzg.BytesPerCell * Ckzg.CellsPerExtBlob);
            for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
            {
                Ckzg.ComputeCells(allCells, wrapper.Blobs[blobIndex], KzgPolynomialCommitments.CkzgSetup);
                int outIndex = blobIndex * cellsPerBlob;
                foreach (int cellIndex in availableMask.EnumerateSetBits())
                {
                    cells[outIndex] = allCells.Slice(cellIndex * Ckzg.BytesPerCell, Ckzg.BytesPerCell).ToArray();
                    outIndex++;
                }
            }

            return true;
        }

        if (wrapper.Cells is null)
        {
            cells = [];
            return false;
        }

        cells = SelectFlattenedCells(wrapper.Cells, wrapper.CellMask, availableMask, blobCount);
        return true;
    }

    /// <summary>
    /// Computes the mask of cells present (non-empty) for every blob in a flattened cell array,
    /// validating cell sizes along the way.
    /// </summary>
    /// <returns><c>false</c> when the array shape or any cell size is invalid.</returns>
    public static bool TryGetPresentCellMask(byte[][] flattenedCells, BlobCellMask cellMask, int blobCount, out BlobCellMask presentMask)
    {
        presentMask = BlobCellMask.Empty;
        int cellsPerBlob = cellMask.Count;
        if (cellsPerBlob == 0 || blobCount <= 0 || flattenedCells.Length != blobCount * cellsPerBlob)
        {
            return false;
        }

        int position = 0;
        foreach (int cellIndex in cellMask.EnumerateSetBits())
        {
            bool presentForAllBlobs = true;
            for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
            {
                int length = flattenedCells[blobIndex * cellsPerBlob + position].Length;
                if (length is not 0 and not Ckzg.BytesPerCell)
                {
                    presentMask = BlobCellMask.Empty;
                    return false;
                }

                presentForAllBlobs &= length == Ckzg.BytesPerCell;
            }

            if (presentForAllBlobs)
            {
                presentMask |= new BlobCellMask(UInt128.One << cellIndex);
            }

            position++;
        }

        return true;
    }

    /// <summary>
    /// Copies the columns of <paramref name="selectedMask"/> (a subset of <paramref name="sourceMask"/>)
    /// out of a flattened cell array into a new flattened array.
    /// </summary>
    public static byte[][] SelectFlattenedCells(byte[][] flattenedCells, BlobCellMask sourceMask, BlobCellMask selectedMask, int blobCount)
    {
        int sourceCellsPerBlob = sourceMask.Count;
        int selectedCellsPerBlob = selectedMask.Count;
        byte[][] cells = new byte[blobCount * selectedCellsPerBlob][];
        for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
        {
            int outIndex = blobIndex * selectedCellsPerBlob;
            int sourceIndex = blobIndex * sourceCellsPerBlob;
            int sourcePosition = 0;
            foreach (int cellIndex in sourceMask.EnumerateSetBits())
            {
                if (selectedMask.Contains(cellIndex))
                {
                    cells[outIndex++] = flattenedCells[sourceIndex + sourcePosition];
                }

                sourcePosition++;
            }
        }

        return cells;
    }

    public static byte[][] SelectProofs(ShardBlobNetworkWrapper wrapper, int blobIndex, BlobCellMask requestedMask)
    {
        BlobCellMask availableMask = wrapper.GetAvailableCellMask() & requestedMask;
        byte[][] proofs = new byte[availableMask.Count][];
        int i = 0;
        foreach (int cellIndex in availableMask.EnumerateSetBits())
        {
            proofs[i++] = wrapper.Proofs[blobIndex * Ckzg.CellsPerExtBlob + cellIndex];
        }

        return proofs;
    }

    public static byte[][] MergeFlattenedCells(
        byte[][]? currentCells,
        BlobCellMask currentMask,
        byte[][] addedCells,
        BlobCellMask addedMask,
        int blobCount)
    {
        if (currentCells is null || currentMask.IsEmpty)
        {
            return addedCells;
        }

        BlobCellMask mergedMask = currentMask | addedMask;
        int currentCellsPerBlob = currentMask.Count;
        int addedCellsPerBlob = addedMask.Count;
        int mergedCellsPerBlob = mergedMask.Count;
        byte[][] mergedCells = new byte[blobCount * mergedCellsPerBlob][];

        for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
        {
            int mergedOffset = blobIndex * mergedCellsPerBlob;
            int currentOffset = blobIndex * currentCellsPerBlob;
            int addedOffset = blobIndex * addedCellsPerBlob;
            int currentPosition = 0;
            int addedPosition = 0;
            int mergedPosition = 0;

            foreach (int cellIndex in mergedMask.EnumerateSetBits())
            {
                if (addedMask.Contains(cellIndex))
                {
                    mergedCells[mergedOffset + mergedPosition] = addedCells[addedOffset + addedPosition++];
                }
                else if (currentMask.Contains(cellIndex))
                {
                    mergedCells[mergedOffset + mergedPosition] = currentCells[currentOffset + currentPosition];
                }

                if (currentMask.Contains(cellIndex))
                {
                    currentPosition++;
                }

                mergedPosition++;
            }
        }

        return mergedCells;
    }
}
