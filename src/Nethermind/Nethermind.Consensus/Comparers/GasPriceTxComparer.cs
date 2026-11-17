// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Comparers
{
    public class GasPriceTxComparer(IBlockFinder blockFinder, ISpecProvider specProvider) : IComparer<MempoolTransaction>
    {
        private readonly IBlockFinder _blockFinder = blockFinder;
        private readonly ISpecProvider _specProvider = specProvider;

        public int Compare(MempoolTransaction? x, MempoolTransaction? y)
        {
            if (ReferenceEquals(x, y)) return TxComparisonResult.Equal;
            if (y is null) return TxComparisonResult.YFirst;
            if (x is null) return TxComparisonResult.XFirst;

            // if gas bottleneck was calculated, it's the highest priority for sorting
            // if not, different method of sorting by gas price is needed
            if (x.GasBottleneck is not null && y.GasBottleneck is not null)
            {
                return y!.GasBottleneck.Value.CompareTo(x!.GasBottleneck.GetValueOrDefault());
            }

            // When we're adding Tx to TxPool, we don't know the base fee of the block in which transaction will be added.
            // We can get a base fee from the current head.
            Block block = _blockFinder.Head;
            bool isEip1559Enabled = _specProvider.GetSpecFor1559(block?.Number ?? 0UL).IsEip1559Enabled;

            return GasPriceTxComparerHelper.Compare(x.Transaction, y.Transaction, (block?.Header.BaseFeePerGas).GetValueOrDefault(), isEip1559Enabled);
        }
    }
}
