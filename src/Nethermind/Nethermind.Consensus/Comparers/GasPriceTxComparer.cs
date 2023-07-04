// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Comparers
{
    public class GasPriceTxComparer : IComparer<Transaction>
    {
        private readonly IBlockFinder _blockFinder;
        private readonly ISpecProvider _specProvider;

        public GasPriceTxComparer(IBlockFinder blockFinder, ISpecProvider specProvider)
        {
            _blockFinder = blockFinder;
            _specProvider = specProvider;
        }

        public int Compare(Transaction? x, Transaction? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            // if gas bottleneck was calculated, it's highest priority for sorting
            // if not, different method of sorting by gas price is needed
            if (x.GasBottleneck is not null && y.GasBottleneck is not null)
            {
                return y!.GasBottleneck.Value.CompareTo(x!.GasBottleneck.GetValueOrDefault());
            }

            // When we're adding Tx to TxPool we don't know the base fee of the block in which transaction will be added.
            // We can get a base fee from the current head.
            Block block = _blockFinder.Head;
            bool isEip1559Enabled = _specProvider.GetSpecFor1559(block?.Number ?? 0L).IsEip1559Enabled;

            return GasPriceTxComparerHelper.Compare(x, y, (block?.Header.BaseFeePerGas).GetValueOrDefault(), isEip1559Enabled);
        }
    }
}
