// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;

namespace Nethermind.Consensus
{
    public static class TxFilterBuilders
    {
        /// <param name="slots">Places the next block for the EIP-8198 base fee; see <see cref="BaseFeeTxFilter"/>.</param>
        public static IMinGasPriceTxFilter CreateStandardMinGasPriceTxFilter(IBlocksConfig blocksConfig, SlotSchedule? slots = null)
            => new MinGasPriceTxFilter(blocksConfig, slots);
    }
}
