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

        cells = new byte[blobCount * cellsPerBlob][];
        int sourceCellsPerBlob = wrapper.CellMask.Count;
        for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
        {
            int outIndex = blobIndex * cellsPerBlob;
            int sourcePosition = 0;
            foreach (int cellIndex in wrapper.CellMask.EnumerateSetBits())
            {
                if (availableMask.Contains(cellIndex))
                {
                    cells[outIndex] = wrapper.Cells[blobIndex * sourceCellsPerBlob + sourcePosition];
                    outIndex++;
                }

                sourcePosition++;
            }
        }

        return true;
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
