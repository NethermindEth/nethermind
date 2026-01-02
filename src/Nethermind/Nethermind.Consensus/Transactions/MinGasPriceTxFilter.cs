// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions;

/// <summary>The filter for transactions below minimum gas price threshold. It is the minimal value for gas that the miner/validator would receive.
/// Before 1559: EffectivePriorityFeePerGas = transaction.GasPrice.
/// After 1559: EffectivePriorityFeePerGas = transaction.EffectiveGasPrice - BaseFee.</summary>
public class MinGasPriceTxFilter(IBlocksConfig blocksConfig) : IMinGasPriceTxFilter
{
    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec)
        => IsAllowed(tx, parentHeader, blocksConfig.MinGasPrice, currentSpec);

    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader? parentHeader, in UInt256 minGasPriceFloor, IReleaseSpec spec)
    {
        UInt256 premiumPerGas = tx.GasPrice;
        UInt256 baseFeePerGas = UInt256.Zero;
        if (spec.IsEip1559Enabled)
        {
            baseFeePerGas = BaseFeeCalculator.Calculate(parentHeader, spec);
            tx.TryCalculatePremiumPerGas(baseFeePerGas, out premiumPerGas);
        }

        bool allowed = premiumPerGas >= minGasPriceFloor;
        return allowed
            ? AcceptTxResult.Accepted
            : AcceptTxResult.FeeTooLow.WithMessage(
                $"EffectivePriorityFeePerGas too low {premiumPerGas} < {minGasPriceFloor}, BaseFee: {baseFeePerGas}");
    }
}
