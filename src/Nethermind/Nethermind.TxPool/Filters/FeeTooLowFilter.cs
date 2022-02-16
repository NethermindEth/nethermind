//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions where gas fee properties were set too low or where the sender has not enough balance.
    /// </summary>
    internal class FeeTooLowFilter : IIncomingTxFilter
    {
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IChainHeadInfoProvider _headInfo;
        private readonly IAccountStateProvider _accounts;
        private readonly TxDistinctSortedPool _txs;
        private readonly ILogger _logger;

        public FeeTooLowFilter(IChainHeadInfoProvider headInfo, IAccountStateProvider accountStateProvider, TxDistinctSortedPool txs, ILogger logger)
        {
            _specProvider = headInfo.SpecProvider;
            _headInfo = headInfo;
            _accounts = accountStateProvider;
            _txs = txs;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxHandlingOptions handlingOptions)
        {
            IReleaseSpec spec = _specProvider.GetCurrentHeadSpec();
            Account account = _accounts.GetAccount(tx.SenderAddress!);
            UInt256 balance = account.Balance;
            UInt256 affordableGasPrice = tx.CalculateAffordableGasPrice(spec.IsEip1559Enabled, _headInfo.CurrentBaseFee, balance);
            bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) != TxHandlingOptions.PersistentBroadcast;
            
            if (_txs.IsFull()
                && _txs.TryGetLast(out Transaction? lastTx)
                && affordableGasPrice <= lastTx?.GasBottleneck
                && isNotLocal)
            {
                Metrics.PendingTransactionsTooLowFee++;
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price.");
                return AcceptTxResult.FeeTooLow.WithMessage($"FeePerGas needs to be higher than {lastTx.GasBottleneck.Value} to be added to the TxPool. Affordable FeePerGas of rejected tx: {affordableGasPrice}.");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
