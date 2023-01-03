// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions which gas payments overflow uint256 or simply exceed sender balance
    /// </summary>
    internal class TooExpensiveTxFilter : IIncomingTxFilter
    {
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IChainHeadInfoProvider _headInfo;
        private readonly TxDistinctSortedPool _txs;
        private readonly ILogger _logger;

        public TooExpensiveTxFilter(IChainHeadInfoProvider headInfo, TxDistinctSortedPool txs, ILogger logger)
        {
            _specProvider = headInfo.SpecProvider;
            _headInfo = headInfo;
            _txs = txs;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            IReleaseSpec spec = _specProvider.GetCurrentHeadSpec();
            Account account = state.SenderAccount;
            UInt256 balance = account.Balance;
            UInt256 cumulativeCost = UInt256.Zero;
            bool overflow = false;
            Transaction[] transactions = _txs.GetBucketSnapshot(tx.SenderAddress!); // since unknownSenderFilter will run before this one

            for (int i = 0; i < transactions.Length; i++)
            {
                if (transactions[i].Nonce < account.Nonce)
                {
                    continue;
                }

                if (transactions[i].Nonce < tx.Nonce)
                {
                    overflow |= UInt256.MultiplyOverflow(
                        transactions[i].CalculateEffectiveGasPrice(spec.IsEip1559Enabled, _headInfo.CurrentBaseFee),
                        (UInt256)transactions[i].GasLimit,
                        out UInt256 txCost);

                    overflow |= UInt256.AddOverflow(cumulativeCost, txCost, out cumulativeCost);
                    overflow |= UInt256.AddOverflow(cumulativeCost, transactions[i].Value, out cumulativeCost);
                }
                else
                {
                    break;
                }
            }

            UInt256 affordableGasPrice = tx.CalculateAffordableGasPrice(spec.IsEip1559Enabled, _headInfo.CurrentBaseFee, balance > cumulativeCost ? balance - cumulativeCost : 0);

            overflow |= spec.IsEip1559Enabled && UInt256.AddOverflow(tx.MaxPriorityFeePerGas, tx.MaxFeePerGas, out _);
            overflow |= UInt256.MultiplyOverflow(affordableGasPrice, (UInt256)tx.GasLimit, out UInt256 cost);
            overflow |= UInt256.AddOverflow(cost, tx.Value, out cost);
            overflow |= UInt256.AddOverflow(cost, cumulativeCost, out cumulativeCost);
            if (overflow)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, cost overflow.");
                return AcceptTxResult.Int256Overflow;
            }

            if (balance < cumulativeCost)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, insufficient funds.");
                return AcceptTxResult.InsufficientFunds.WithMessage($"Account balance: {balance}, cumulative cost: {cumulativeCost}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
