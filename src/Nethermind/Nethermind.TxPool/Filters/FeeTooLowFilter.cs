// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions where gas fee properties were set too low.
    /// </summary>
    internal sealed class FeeTooLowFilter(IChainHeadInfoProvider headInfo, TxDistinctSortedPool txs, TxDistinctSortedPool blobTxs, bool thereIsPriorityContract, ILogger logger) : IIncomingTxFilter
    {
        private readonly IChainHeadSpecProvider _specProvider = headInfo.SpecProvider;
        private readonly IChainHeadInfoProvider _headInfo = headInfo;
        private readonly TxDistinctSortedPool _txs = txs;
        private readonly TxDistinctSortedPool _blobTxs = blobTxs;
        private readonly bool _thereIsPriorityContract = thereIsPriorityContract;
        private readonly ILogger _logger = logger;

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            bool isLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) != 0;
            if (isLocal)
            {
                return AcceptTxResult.Accepted;
            }

            IReleaseSpec spec = _specProvider.GetCurrentHeadSpec();
            bool isEip1559Enabled = spec.IsEip1559Enabled;
            UInt256 affordableGasPrice = tx.CalculateGasPrice(isEip1559Enabled, _headInfo.CurrentBaseFee);
            // Don't accept zero fee txns even if pool is empty as will never run
            if (isEip1559Enabled && !_thereIsPriorityContract && !tx.IsFree() && affordableGasPrice.IsZero)
            {
                Metrics.PendingTransactionsTooLowFee++;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price with options {handlingOptions} from {new StackTrace()}");
                return AcceptTxResult.FeeTooLow;
            }

            TxDistinctSortedPool relevantPool = (tx.SupportsBlobs ? _blobTxs : _txs);
            if (relevantPool.IsFull() && relevantPool.TryGetLast(out Transaction? lastTx)
                && affordableGasPrice <= lastTx?.GasBottleneck)
            {
                Metrics.PendingTransactionsTooLowFee++;
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price with options {handlingOptions} from {new StackTrace()}");
                }

                return AcceptTxResult.FeeTooLow;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
