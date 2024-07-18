// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Comparers
{
    public class TransactionComparerProvider : ITransactionComparerProvider
    {
        private readonly ISpecProvider _specProvider;
        private readonly IBlockFinder _blockFinder;

        // we're caching default comparer
        private IComparer<Transaction>? _defaultComparer = null;

        public TransactionComparerProvider(ISpecProvider specProvider, IBlockFinder blockFinder)
        {
            _specProvider = specProvider;
            _blockFinder = blockFinder;
        }

        public IComparer<Transaction> GetDefaultComparer()
        {
            if (_defaultComparer is null)
            {
                IComparer<Transaction> gasPriceComparer = new GasPriceTxComparer(_blockFinder, _specProvider);
                _defaultComparer = gasPriceComparer
                    .ThenBy(CompareTxByTimestamp.Instance)
                    .ThenBy(CompareTxByPoolIndex.Instance)
                    .ThenBy(CompareTxByGasLimit.Instance);
            }

            return _defaultComparer;
        }

        public IComparer<Transaction> GetDefaultProducerComparer(BlockPreparationContext blockPreparationContext)
        {
            IComparer<Transaction> gasPriceComparer =
                new GasPriceTxComparerForProducer(blockPreparationContext, _specProvider);
            return gasPriceComparer
                .ThenBy(CompareTxByTimestamp.Instance)
                .ThenBy(CompareTxByPoolIndex.Instance)
                .ThenBy(CompareTxByGasLimit.Instance);
        }
    }
}
