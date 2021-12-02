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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    // Class is used only for nonposdao AuRa chains. These transactions will be paid by the validator.
    public class TxGasPriceSender : ITxSender
    {
        private readonly ITxSender _txSender;
        private readonly IGasPriceOracle _gasPriceOracle;
        private readonly uint _percentDelta;

        public TxGasPriceSender(
            ITxSender txSender,
            IGasPriceOracle gasPriceOracle,
            uint percentDelta = TxGasPriceSenderConstants.DefaultPercentMultiplier)
        {
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
            _percentDelta = percentDelta;
        }

        public ValueTask<(Keccak, AcceptTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            UInt256 gasPriceEstimated = _gasPriceOracle.GetGasPriceEstimate() * _percentDelta / 100;
            tx.DecodedMaxFeePerGas = gasPriceEstimated;
            tx.GasPrice = gasPriceEstimated;
            return _txSender.SendTransaction(tx, txHandlingOptions);
        }
    }
}
