// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Comparers
{
    public class TransactionComparerProvider(ISpecProvider specProvider, IBlockFinder blockFinder)
        : ITransactionComparerProvider
    {
        // we're caching default comparer
        private IComparer<Transaction>? _defaultComparer = null;

        public IComparer<Transaction> GetDefaultComparer() =>
            _defaultComparer ??= new GasPriceTxComparer(blockFinder, specProvider)
                .ThenBy(CompareTxByTimestamp.Instance)
                .ThenBy(CompareTxByPoolIndex.Instance)
                .ThenBy(CompareTxByGasLimit.Instance);

        public IComparer<Transaction> GetDefaultProducerComparer(BlockPreparationContext blockPreparationContext) =>
            new GasPriceTxComparerForProducer(blockPreparationContext, specProvider)
                .ThenBy(BlobTxPriorityComparer.Instance)
                .ThenBy(CompareTxByTimestamp.Instance)
                .ThenBy(CompareTxByPoolIndex.Instance)
                .ThenBy(CompareTxByGasLimit.Instance);
    }
}
