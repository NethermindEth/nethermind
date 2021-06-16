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

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class TxGasPriceSender : ITxSender
    {
        private readonly ITxSender _txSender;
        private readonly ITxPool _txPool;
        private readonly IMiningConfig _miningConfig;
        private readonly uint _percentDelta;

        public TxGasPriceSender(ITxSender txSender, ITxPool txPool, IMiningConfig miningConfig, uint percentDelta = TxGasPriceSenderConstants.DefaultPercentMultiplier)
        {
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _miningConfig = miningConfig ?? throw new ArgumentNullException(nameof(miningConfig));
            _percentDelta = percentDelta;
        }

        public ValueTask<(Keccak, AddTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            UInt256 minGasPrice =  CurrentMinGasPrice();
            UInt256 txGasPrice = minGasPrice * _percentDelta / 100;
            tx.GasPrice = UInt256.Max(txGasPrice, _miningConfig.MinGasPrice);
            return _txSender.SendTransaction(tx, txHandlingOptions);
        }

        private UInt256 CurrentMinGasPrice() =>
            _txPool.GetPendingTransactions()
                .Select(t => t.GasPrice)
                .Where(g => g > UInt256.Zero)
                .DefaultIfEmpty(TxGasPriceSenderConstants.DefaultGasPrice)
                .Min();
    }
}
