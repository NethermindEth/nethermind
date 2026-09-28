// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.TxPool.Filters;

internal sealed class BlobProofsTxFilter : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        // EIP-8141: a blob-carrying frame tx (type 6) shares the EIP-7594 wrapper with type-3, and
        // MalformedTxFilter skips proofs, so gating on the type alone would leave type-6 unverified.
        if (tx is not { NetworkWrapper: ShardBlobNetworkWrapper wrapper } || !(tx.SupportsBlobs || tx.CarriesBlobs))
        {
            return AcceptTxResult.Accepted;
        }

        if (wrapper.Version is not (ProofVersion.V0 or ProofVersion.V1))
        {
            return AcceptTxResult.InvalidBlobProofs;
        }

        if (!wrapper.HasFullBlobs()
            && (wrapper.Cells is not { Length: > 0 } || wrapper.CellMask.IsEmpty))
        {
            return AcceptTxResult.IncompleteBlobData;
        }

        if (!IBlobProofsManager.For(wrapper.Version).ValidateProofs(wrapper))
        {
            return AcceptTxResult.InvalidBlobProofs;
        }

        if (wrapper.Version == ProofVersion.V1
            && !wrapper.HasFullBlobs()
            && wrapper.CellMask.Count >= BlobCellsHelper.RequiredCellsForRecovery)
        {
            if (!BlobCellsHelper.TryRecoverBlobsFromVerifiedCells(wrapper, out ShardBlobNetworkWrapper recoveredWrapper))
            {
                return AcceptTxResult.InvalidBlobProofs;
            }

            tx.NetworkWrapper = recoveredWrapper;
            tx.ClearLengthCache();
        }

        return AcceptTxResult.Accepted;
    }
}
