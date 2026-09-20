// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>Builds real KZG cells/proofs/commitments for one blob, for DAS verification/reconstruction tests.</summary>
internal static class DataColumnKzgFixture
{
    /// <summary>
    /// A deterministic, always-valid blob: every 32-byte field element has only its low byte set,
    /// which is trivially below the BLS modulus regardless of the element's byte order.
    /// </summary>
    public static byte[] MakeBlob(byte seed)
    {
        byte[] blob = new byte[Ckzg.BytesPerBlob];
        for (int element = 0; element < Ckzg.BytesPerBlob / 32; element++)
        {
            blob[(element * 32) + 31] = (byte)(seed + element);
        }
        return blob;
    }

    public readonly record struct BlobFixture(byte[] Commitment, byte[] Cells, byte[] Proofs);

    public static BlobFixture BuildBlob(byte seed)
    {
        byte[] blob = MakeBlob(seed);
        byte[] commitment = new byte[Ckzg.BytesPerCommitment];
        Ckzg.BlobToKzgCommitment(commitment, blob, DasKzg.Handle);

        byte[] cells = new byte[Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell];
        byte[] proofs = new byte[Ckzg.CellsPerExtBlob * Ckzg.BytesPerProof];
        Ckzg.ComputeCellsAndKzgProofs(cells, proofs, blob, DasKzg.Handle);

        return new BlobFixture(commitment, cells, proofs);
    }

    public static SszBlobCell CellAt(BlobFixture blob, int columnIndex) =>
        SszBlobCell.FromSpan(blob.Cells.AsSpan(columnIndex * Ckzg.BytesPerCell, Ckzg.BytesPerCell));

    public static SszKzgCommitment ProofAt(BlobFixture blob, int columnIndex) =>
        SszKzgCommitment.FromSpan(blob.Proofs.AsSpan(columnIndex * Ckzg.BytesPerProof, Ckzg.BytesPerProof));

    public static SszKzgCommitment CommitmentOf(BlobFixture blob) =>
        SszKzgCommitment.FromSpan(blob.Commitment);
}
