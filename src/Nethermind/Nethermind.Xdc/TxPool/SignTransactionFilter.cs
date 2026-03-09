// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System.Linq;

namespace Nethermind.Xdc.TxPool;

internal sealed class SignTransactionFilter(ISnapshotManager snapshotManager, IBlockTree blockTree, ISpecProvider specProvider) : IIncomingTxFilter
{
    private (long, IXdcReleaseSpec) GetSpecAndHeader()
    {
        XdcBlockHeader header = (XdcBlockHeader)blockTree.Head.Header;
        long currentHeaderNumber = header.Number + 1;
        IXdcReleaseSpec xdcSpec = specProvider.GetXdcSpec(currentHeaderNumber);

        return (currentHeaderNumber, xdcSpec);
    }

    private AcceptTxResult ValidateSignTransaction(Transaction tx, long headerNumber, IXdcReleaseSpec xdcSpec)
    {
        if (tx.Data.Length < XdcConstants.SignTransactionDataLength)
        {
            return AcceptTxResult.Invalid.WithMessage("Sign transaction data length is less than required length");
        }

        UInt256 blkNumber = new UInt256(tx.Data.Span.Slice(4, 32), true);
        if (blkNumber > headerNumber || blkNumber <= (headerNumber - (xdcSpec.EpochLength * 2)))
        {
            // Invalid block number in special transaction data
            return AcceptTxResult.Invalid.WithMessage("Sign transaction block number is out of range");
        }

        return AcceptTxResult.Accepted;
    }

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        (long headerNumber, IXdcReleaseSpec spec) = GetSpecAndHeader();

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

        return AcceptTxResult.Accepted;
    }

    private static bool IsEpochCandidate(Snapshot? snapshot, Address? senderAddress) =>
        snapshot is not null && senderAddress is not null &&
        snapshot.NextEpochCandidates.Contains(senderAddress);
}
