// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc.TxPool;

internal sealed class SignTransactionFilter(ISnapshotManager snapshotManager, IBlockTree blockTree, ISpecProvider specProvider) : IIncomingTxFilter
{
    private AcceptTxResult ValidateSignTransaction(Transaction tx, ulong headerNumber, IXdcReleaseSpec xdcSpec)
    {
        if (tx.Data.Length < XdcConstants.SignTransactionDataLength)
        {
            return AcceptTxResult.Invalid.WithMessage("Sign transaction data length is less than required length");
        }

        UInt256 blkNumber = new(tx.Data.Span.Slice(4, 32), true);

        ulong epochWindow = xdcSpec.EpochLength * 2;
        UInt256 lowerBound = headerNumber.SaturatingSub(epochWindow);

        if (blkNumber > headerNumber || blkNumber <= lowerBound)
        {
            // Invalid block number in special transaction data
            return AcceptTxResult.Invalid.WithMessage("Sign transaction block number is out of range");
        }

        return AcceptTxResult.Accepted;
    }

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (blockTree.Head is null)
            return AcceptTxResult.Syncing;

        XdcBlockHeader header = (XdcBlockHeader)blockTree.Head.Header;
        ulong headerNumber = header.Number + 1;
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(headerNumber);

        if (!tx.IsSpecialTransaction(spec))
        {
            return AcceptTxResult.Accepted;
        }
        if (tx.IsSignTransaction(spec))
        {
            AcceptTxResult result = ValidateSignTransaction(tx, headerNumber, spec);
            if (result != AcceptTxResult.Accepted)
            {
                return result;
            }
        }

        Snapshot? snapshot = snapshotManager.GetSnapshotByBlockNumber(headerNumber, spec);

        if (!IsEpochCandidate(snapshot, tx.SenderAddress))
        {
            return AcceptTxResult.Invalid.WithMessage("Special transaction sender is not an epoch candidate");
        }

        tx.IsServiceTransaction = true;

        return AcceptTxResult.Accepted;
    }

    private static bool IsEpochCandidate(Snapshot? snapshot, Address? senderAddress) =>
        snapshot is not null && senderAddress is not null &&
        snapshot.NextEpochCandidates.AsSpan().IndexOf(senderAddress) != -1;
}
