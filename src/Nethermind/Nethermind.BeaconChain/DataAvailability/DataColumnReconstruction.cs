// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

        using ArrayPoolSpan<byte> heldCellsForRow = new(distinct * Ckzg.BytesPerCell);
        using ArrayPoolSpan<byte> recoveredCells = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell);
        using ArrayPoolSpan<byte> recoveredProofs = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerProof);

        for (int b = 0; b < blobCount; b++)
        {
            int pos = 0;
            for (int c = 0; c < Eip7594DasConstants.NumberOfColumns; c++)
            {
                if (byIndex[c] is { } held)
                {
                    held.Column![b].AsSpan().CopyTo(heldCellsForRow.Slice(pos * Ckzg.BytesPerCell, Ckzg.BytesPerCell));
                    pos++;
                }
            }

            try
            {
                Ckzg.RecoverCellsAndKzgProofs(recoveredCells, recoveredProofs, heldColumnIndices, heldCellsForRow, distinct, DasKzgSetup.Handle);
            }
            catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
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
}
