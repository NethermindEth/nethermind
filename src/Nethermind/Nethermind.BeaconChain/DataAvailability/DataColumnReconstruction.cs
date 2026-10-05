// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core.Collections;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Fulu <c>recover_matrix</c> (das-core.md): reconstructs the full 128-column extended data matrix
/// from at least <see cref="Eip7594DasConstants.RequiredColumnsForReconstruction"/> distinct held
/// columns of the same block.
/// </summary>
/// <remarks>
/// Mirrors <c>Nethermind.Crypto.BlobCellsHelper.TryRecoverBlobsFromVerifiedCells</c>'s discipline:
/// callers must only pass sidecars that already passed <see cref="DataColumnSidecarVerifier"/>
/// (verify once on receipt, then trust local storage - this does not re-verify KZG proofs). Recovery
/// runs per blob row, since <c>Ckzg.RecoverCellsAndKzgProofs</c> operates on one extended blob's
/// cells at a time.
/// </remarks>
public static class DataColumnReconstruction
{
    /// <summary>
    /// Attempts to recover the full 128-column matrix from <paramref name="heldColumns"/>.
    /// </summary>
    /// <param name="heldColumns">
    /// Distinct-by-index columns of one block. All must carry the same blob count; the returned
    /// matrix's shared fields (commitments, header, inclusion proof) are copied from the first entry.
    /// </param>
    /// <returns>
    /// <c>true</c> with all 128 columns on success. <c>false</c>, unchanged, when fewer than
    /// <see cref="Eip7594DasConstants.RequiredColumnsForReconstruction"/> distinct columns are held,
    /// the columns are shaped inconsistently, or the native recovery call itself fails.
    /// </returns>
    public static bool TryReconstruct(IReadOnlyList<DataColumnSidecar> heldColumns, out DataColumnSidecar[] fullMatrix)
    {
        fullMatrix = [];
        if (heldColumns.Count == 0
            || heldColumns[0].KzgCommitments is not { Length: > 0 } commitments
            || heldColumns[0].SignedBlockHeader is not { } header
            || heldColumns[0].KzgCommitmentsInclusionProof is not { } inclusionProof)
        {
            return false;
        }

        int blobCount = commitments.Length;
        DataColumnSidecar?[] byIndex = new DataColumnSidecar?[Eip7594DasConstants.NumberOfColumns];
        int distinct = 0;
        foreach (DataColumnSidecar sidecar in heldColumns)
        {
            if (sidecar.Index >= (ulong)Eip7594DasConstants.NumberOfColumns
                || sidecar.Column is not { Length: > 0 } column || column.Length != blobCount
                || sidecar.KzgProofs is not { } proofs || proofs.Length != blobCount)
            {
                return false;
            }

            // Columns of two different blocks that happen to share a blob count would otherwise
            // recover into a matrix belonging to neither.
            if (sidecar.SignedBlockHeader?.Message?.BodyRoot != header.Message?.BodyRoot)
            {
                return false;
            }

            int index = (int)sidecar.Index;
            if (byIndex[index] is null)
            {
                byIndex[index] = sidecar;
                distinct++;
            }
        }

        if (distinct < Eip7594DasConstants.RequiredColumnsForReconstruction)
        {
            return false;
        }

        using ArrayPoolSpan<ulong> heldColumnIndices = new(distinct);
        int w = 0;
        for (int c = 0; c < Eip7594DasConstants.NumberOfColumns; c++)
        {
            if (byIndex[c] is not null)
            {
                heldColumnIndices[w++] = (ulong)c;
            }
        }

        SszBlobCell[][] recoveredColumnCells = new SszBlobCell[Eip7594DasConstants.NumberOfColumns][];
        SszKzgCommitment[][] recoveredColumnProofs = new SszKzgCommitment[Eip7594DasConstants.NumberOfColumns][];
        for (int c = 0; c < Eip7594DasConstants.NumberOfColumns; c++)
        {
            recoveredColumnCells[c] = new SszBlobCell[blobCount];
            recoveredColumnProofs[c] = new SszKzgCommitment[blobCount];
        }

        SszBlobCell[]?[] cellsByColumn = new SszBlobCell[]?[Eip7594DasConstants.NumberOfColumns];
        for (int c = 0; c < Eip7594DasConstants.NumberOfColumns; c++)
        {
            cellsByColumn[c] = byIndex[c]?.Column;
        }

        using ArrayPoolSpan<byte> heldCellsForRow = new(distinct * Ckzg.BytesPerCell);
        using ArrayPoolSpan<byte> recoveredCells = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell);
        using ArrayPoolSpan<byte> recoveredProofs = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerProof);

        for (int b = 0; b < blobCount; b++)
        {
            if (!TryRecoverRow(cellsByColumn, heldColumnIndices, b, heldCellsForRow, recoveredCells, recoveredProofs))
            {
                fullMatrix = [];
                return false;
            }

            for (int c = 0; c < Eip7594DasConstants.NumberOfColumns; c++)
            {
                recoveredColumnCells[c][b] = SszBlobCell.FromSpan(recoveredCells.Slice(c * Ckzg.BytesPerCell, Ckzg.BytesPerCell));
                recoveredColumnProofs[c][b] = SszKzgCommitment.FromSpan(recoveredProofs.Slice(c * Ckzg.BytesPerProof, Ckzg.BytesPerProof));
            }
        }

        DataColumnSidecar[] matrix = new DataColumnSidecar[Eip7594DasConstants.NumberOfColumns];
        for (int c = 0; c < Eip7594DasConstants.NumberOfColumns; c++)
        {
            matrix[c] = new DataColumnSidecar
            {
                Index = (ulong)c,
                Column = recoveredColumnCells[c],
                KzgCommitments = commitments,
                KzgProofs = recoveredColumnProofs[c],
                SignedBlockHeader = header,
                KzgCommitmentsInclusionProof = inclusionProof,
            };
        }

        fullMatrix = matrix;
        return true;
    }

    /// <summary>Recovers the blobs at <paramref name="rows"/> of one block from the cells of at least half of its columns.</summary>
    /// <remarks>
    /// fulu/das-core.md recover_matrix, keeping the first half of each row: in consensus-specs v1.6.0
    /// fulu/polynomial-commitments-sampling.md coset_for_cell, the first <c>CELLS_PER_EXT_BLOB / 2</c> cells evaluate the
    /// blob's own bit-reversed domain, so in order they are the blob. When those columns are all held, no KZG work is done.
    /// Like <see cref="TryReconstruct"/>, this trusts that every cell passed verification before it was stored.
    /// </remarks>
    /// <param name="cellsByColumn">
    /// <c>NUMBER_OF_COLUMNS</c> entries, <c>null</c> where the column is not held; a held column has a cell for every row in <paramref name="rows"/>.
    /// </param>
    /// <param name="rows">The blob indices to recover; the result keeps their order.</param>
    /// <param name="blobs">The recovered blobs, one per entry of <paramref name="rows"/>.</param>
    /// <returns><c>false</c> when fewer than half of the columns are held, or the native recovery fails.</returns>
    internal static bool TryRecoverBlobs(SszBlobCell[]?[] cellsByColumn, IReadOnlyList<int> rows, out byte[][] blobs)
    {
        blobs = [];
        const int systematicColumns = Eip7594DasConstants.RequiredColumnsForReconstruction;
        if (cellsByColumn.Length != Eip7594DasConstants.NumberOfColumns)
        {
            return false;
        }

        using ArrayPoolSpan<ulong> heldColumnIndices = new(systematicColumns);
        int held = 0;
        for (int c = 0; c < Eip7594DasConstants.NumberOfColumns && held < systematicColumns; c++)
        {
            if (cellsByColumn[c] is not null)
            {
                heldColumnIndices[held++] = (ulong)c;
            }
        }

        if (held < systematicColumns)
        {
            return false;
        }

        byte[][] recovered = new byte[rows.Count][];
        if (heldColumnIndices[systematicColumns - 1] == systematicColumns - 1)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                byte[] blob = recovered[i] = new byte[Ckzg.BytesPerBlob];
                for (int c = 0; c < systematicColumns; c++)
                {
                    cellsByColumn[c]![rows[i]].AsSpan().CopyTo(blob.AsSpan(c * Ckzg.BytesPerCell, Ckzg.BytesPerCell));
                }
            }

            blobs = recovered;
            return true;
        }

        using ArrayPoolSpan<byte> heldCellsForRow = new(systematicColumns * Ckzg.BytesPerCell);
        using ArrayPoolSpan<byte> recoveredCells = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell);
        using ArrayPoolSpan<byte> recoveredProofs = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerProof);
        for (int i = 0; i < rows.Count; i++)
        {
            if (!TryRecoverRow(cellsByColumn, heldColumnIndices, rows[i], heldCellsForRow, recoveredCells, recoveredProofs))
            {
                return false;
            }

            recovered[i] = recoveredCells.Slice(0, Ckzg.BytesPerBlob).ToArray();
        }

        blobs = recovered;
        return true;
    }

    /// <summary>Runs <c>recover_cells_and_kzg_proofs</c> on blob <paramref name="row"/> from the cells of the columns at <paramref name="heldColumnIndices"/>.</summary>
    /// <returns><c>false</c> when the native recovery call fails.</returns>
    private static bool TryRecoverRow(SszBlobCell[]?[] cellsByColumn, Span<ulong> heldColumnIndices, int row,
        Span<byte> heldCellsForRow, Span<byte> recoveredCells, Span<byte> recoveredProofs)
    {
        for (int i = 0; i < heldColumnIndices.Length; i++)
        {
            cellsByColumn[heldColumnIndices[i]]![row].AsSpan().CopyTo(heldCellsForRow.Slice(i * Ckzg.BytesPerCell, Ckzg.BytesPerCell));
        }

        try
        {
            Ckzg.RecoverCellsAndKzgProofs(recoveredCells, recoveredProofs, heldColumnIndices, heldCellsForRow, heldColumnIndices.Length, DasKzg.Handle);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
