// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.TxPool.Filters;

internal sealed class BlobProofsTxFilter : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (tx is not { Type: TxType.Blob, NetworkWrapper: ShardBlobNetworkWrapper wrapper })
        {
            return AcceptTxResult.Accepted;
        }

        if (wrapper.Version is not (ProofVersion.V0 or ProofVersion.V1)
            || !wrapper.HasFullBlobs() && (wrapper.Cells is not { Length: > 0 } || wrapper.CellMask.IsEmpty)
            || !IBlobProofsManager.For(wrapper.Version).ValidateProofs(wrapper))
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
