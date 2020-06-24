//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class TxGasPriceSender : ITxSender
    {
        private static readonly UInt256 DefaultGasPrice = 20_000_000ul;
        private readonly ITxSender _txSender;
        private readonly ITxPool _txPool;
        private readonly uint _percentDelta;

        public TxGasPriceSender(ITxSender txSender, ITxPool txPool, uint percentDelta = 110)
        {
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _percentDelta = percentDelta;
        }

        public Keccak SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            // TODO: Move to Uint256 when we support division
            ulong minGasPrice = (ulong) CurrentMinGasPrice();
            tx.GasPrice = minGasPrice * _percentDelta / 100;
            return _txSender.SendTransaction(tx, txHandlingOptions);
        }

        private UInt256 CurrentMinGasPrice()
        {
            var transactionsWithGasPrice = _txPool.GetPendingTransactions();
            return transactionsWithGasPrice.Any(t => t.GasPrice > 0) 
                ? transactionsWithGasPrice.Where(t => t.GasPrice > 0).Min(t => t.GasPrice) 
                : DefaultGasPrice;
        }
    }
}