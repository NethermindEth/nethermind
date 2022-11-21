// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus
{
    public static class TxFilterBuilders
    {
        public static IMinGasPriceTxFilter CreateStandardMinGasPriceTxFilter(IMiningConfig miningConfig, ISpecProvider specProvider)
            => new MinGasPriceTxFilter(miningConfig.MinGasPrice, specProvider);
    }
}
