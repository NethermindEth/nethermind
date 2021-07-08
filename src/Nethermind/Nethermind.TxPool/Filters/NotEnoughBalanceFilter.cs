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

namespace Nethermind.TxPool.Filters
{
    internal class NotEnoughBalanceFilter : IIncomingTxFilter
    {
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IChainHeadInfoProvider _headInfo;
        private readonly IAccountStateProvider _accounts;
        private readonly ILogger _logger;

        public NotEnoughBalanceFilter(IChainHeadInfoProvider headInfo, IAccountStateProvider accounts, ILogger logger)
        {
            _headInfo = headInfo;
            _specProvider = _headInfo.SpecProvider;
            _accounts = accounts;
            _logger = logger;
        }

        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            Account account = _accounts.GetAccount(tx.SenderAddress!);
            IReleaseSpec spec = _specProvider.GetSpec();
            UInt256 currentBalance = account.Balance;
            UInt256 affordableGasPrice =
                tx.CalculateAffordableGasPrice(spec.IsEip1559Enabled, _headInfo.CurrentBaseFee, currentBalance);

            bool overflow =
                UInt256.MultiplyOverflow((UInt256)tx.GasLimit, affordableGasPrice, out UInt256 totalAmountFee);
            //overflow |= UInt256.MultiplyOverflow(totalAmountFee, tx.Value, out totalAmountFee);
            if (overflow)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, overflow.");
                return (false, AddTxResult.Int256Overflow);
            }

            if (currentBalance < totalAmountFee)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace(
                        $"Skipped adding transaction {tx.ToString(" ")}, The sender does not have enough funds on the balance.");
                    return (false, AddTxResult.NotEnoughBalance);
                }
            }

            return (true, null);
        }
    }
}
