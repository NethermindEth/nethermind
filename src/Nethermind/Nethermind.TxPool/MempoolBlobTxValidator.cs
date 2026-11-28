// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.TxPool;

/// <summary>
/// Validates the blob sidecar a transaction travels with in its mempool form.
/// </summary>
/// <remarks>Blob proofs are verified separately by <see cref="Filters.BlobProofsTxFilter"/>.</remarks>
public static class MempoolBlobTxValidator
{
    public static ValidationResult IsWellFormed(Transaction transaction, ShardBlobNetworkWrapper? wrapper, IReleaseSpec releaseSpec)
    {
        if (wrapper is null) return ValidationResult.Success;

        // EIP-8141: a blob-carrying frame tx (type 6) shares the EIP-7594 wrapper with type-3.
        if (!transaction.SupportsBlobs && !transaction.CarriesBlobs) return TxErrorMessages.InvalidTransactionForm;

        ValidationResult proofVersion = IsProofVersionValid(wrapper, releaseSpec);
        if (!proofVersion) return proofVersion;

        IBlobProofsVerifier proofsManager = IBlobProofsManager.For(wrapper.Version);
        int blobCount = transaction.BlobVersionedHashes?.Length ?? 0;

        return blobCount != wrapper.Commitments.Length || !proofsManager.ValidateLengths(wrapper) ? TxErrorMessages.InvalidBlobDataSize :
            transaction.BlobVersionedHashes is null || !proofsManager.ValidateHashes(wrapper, transaction.BlobVersionedHashes!) ? TxErrorMessages.InvalidBlobHashes :
            ValidationResult.Success;
    }

    public static ValidationResult IsProofVersionValid(ShardBlobNetworkWrapper? wrapper, IReleaseSpec releaseSpec) =>
        wrapper is not null && wrapper.Version != releaseSpec.BlobProofVersion ? TxErrorMessages.InvalidProofVersion : ValidationResult.Success;
}
