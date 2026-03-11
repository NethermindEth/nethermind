// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc.TxPool;

internal sealed class XdcIncomingTxFilter(
    ISnapshotManager snapshotManager,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    ITrc21StateReader trc21StateReader) : IIncomingTxFilter
{
    private static readonly UInt256 MinGasPrice = (UInt256)XdcConstants.Trc21GasPrice;
    private static readonly UInt256 MinGasPrice50x = (UInt256)XdcConstants.Trc21GasPrice50x;

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
        (XdcBlockHeader currentHead, long headerNumber, IXdcReleaseSpec spec) = XdcTxPoolHelper.GetSpecAndHeader(blockTree, specProvider);

        if (tx.IsSpecialTransaction(spec))
        {
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
        }
        else
        {
            UInt256 minGasPrice = headerNumber >= spec.BlockNumberGas50x
                ? MinGasPrice50x
                : MinGasPrice;
            if (tx.GasPrice < minGasPrice)
            {
                return AcceptTxResult.FeeTooLow;
            }
        }

        if (spec.IsTipTrc21FeeEnabled && tx.To is not null && tx.SenderAddress is not null)
        {
            Dictionary<Address, UInt256> feeCapacities = trc21StateReader.GetFeeCapacities(currentHead);
            if (feeCapacities.ContainsKey(tx.To) &&
                !trc21StateReader.ValidateTransaction(currentHead, tx.SenderAddress, tx.To, tx.Data.Span))
            {
                return AcceptTxResult.InsufficientFunds;
            }
        }

        return AcceptTxResult.Accepted;
    }

    private static bool IsEpochCandidate(Snapshot? snapshot, Address? senderAddress) =>
        snapshot is not null && senderAddress is not null &&
        snapshot.NextEpochCandidates.Contains(senderAddress);
}
