// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Crypto;

namespace Nethermind.TxPool;

internal static class BlobProofsTranslator
{
    /// <param name="tx">The transaction the sidecar belongs to.</param>
    /// <param name="wrapper">The sidecar, replaced by its translation when its proof version differs from <paramref name="currentProofVersion"/>.</param>
    /// <param name="currentProofVersion">The proof version the sidecar must carry.</param>
    public static bool TryTranslateToCurrentProofVersion(Transaction tx, ref ShardBlobNetworkWrapper? wrapper, ProofVersion currentProofVersion)
    {
        if (!tx.CarriesBlobs || wrapper is null || wrapper.Version == currentProofVersion)
        {
            return true;
        }

        if (!ValidateSourceBlobWrapper(tx, wrapper))
        {
            return false;
        }

        ShardBlobNetworkWrapper? translated = (wrapper.Version, currentProofVersion) switch
        {
            (ProofVersion.V0, ProofVersion.V1) => ConvertBlobProofsToCellProofs(wrapper),
            (ProofVersion.V1, ProofVersion.V0) => ConvertCellProofsToBlobProofs(wrapper),
            _ => wrapper
        };

        wrapper = translated ?? wrapper;
        return translated is not null;
    }

    private static ShardBlobNetworkWrapper? ConvertBlobProofsToCellProofs(ShardBlobNetworkWrapper wrapper)
    {
        byte[][] cellProofs = new byte[Ckzg.CellsPerExtBlob * wrapper.Blobs.Length][];

        try
        {
            for (int blobIndex = 0; blobIndex < wrapper.Blobs.Length; blobIndex++)
            {
                using ArrayPoolSpan<byte> cellProofsOfOneBlob = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerProof);
                KzgPolynomialCommitments.ComputeCellProofs(wrapper.Blobs[blobIndex], cellProofsOfOneBlob);

                for (int i = 0; i < Ckzg.CellsPerExtBlob; i++)
                {
                    byte[] cellProof = new byte[Ckzg.BytesPerProof];
                    cellProofsOfOneBlob.Slice(i * Ckzg.BytesPerProof, Ckzg.BytesPerProof).CopyTo(cellProof);
                    cellProofs[blobIndex * Ckzg.CellsPerExtBlob + i] = cellProof;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return null;
        }

        return wrapper with { Proofs = cellProofs, Version = ProofVersion.V1 };
    }

    private static ShardBlobNetworkWrapper? ConvertCellProofsToBlobProofs(ShardBlobNetworkWrapper wrapper)
    {
        byte[][] proofs = new byte[wrapper.Blobs.Length][];

        try
        {
            for (int i = 0; i < wrapper.Blobs.Length; i++)
            {
                byte[] proof = new byte[Ckzg.BytesPerProof];
                KzgPolynomialCommitments.ComputeBlobProof(wrapper.Blobs[i], wrapper.Commitments[i], proof);
                proofs[i] = proof;
            }
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return null;
        }

        return wrapper with { Proofs = proofs, Version = ProofVersion.V0 };
    }

    private static bool ValidateSourceBlobWrapper(Transaction tx, ShardBlobNetworkWrapper wrapper)
    {
        if (tx.BlobVersionedHashes is null)
        {
            return false;
        }

        using ArrayPoolListRef<byte[]> blobVersionedHashes = new(tx.BlobVersionedHashes.Length);
        for (int i = 0; i < tx.BlobVersionedHashes.Length; i++)
        {
            if (tx.BlobVersionedHashes[i] is not byte[] blobVersionedHash)
            {
                return false;
            }

            blobVersionedHashes.Add(blobVersionedHash);
        }

        IBlobProofsVerifier proofVerifier = IBlobProofsManager.For(wrapper.Version);
        return proofVerifier.ValidateLengths(wrapper)
            && proofVerifier.ValidateHashes(wrapper, blobVersionedHashes.AsSpan())
            && proofVerifier.ValidateProofs(wrapper);
    }
}
