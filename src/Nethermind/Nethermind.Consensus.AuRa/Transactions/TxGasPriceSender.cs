// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
