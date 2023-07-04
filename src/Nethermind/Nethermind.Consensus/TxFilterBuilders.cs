// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus
{
    public static class TxFilterBuilders
    {
        public static IMinGasPriceTxFilter CreateStandardMinGasPriceTxFilter(IBlocksConfig blocksConfig, ISpecProvider specProvider)
            => new MinGasPriceTxFilter(blocksConfig, specProvider);
    }
}
