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
        private IComparer<MempoolTransaction>? _defaultComparer = null;

        public IComparer<MempoolTransaction> GetDefaultComparer() =>
            _defaultComparer ??= new GasPriceTxComparer(blockFinder, specProvider)
                .ThenBy(CompareTxByTimestamp.Instance.ForMempool())
                .ThenBy(CompareTxByPoolIndex.Instance)
                .ThenBy(CompareTxByGasLimit.Instance.ForMempool());

        public IComparer<MempoolTransaction> GetDefaultProducerComparer(BlockPreparationContext blockPreparationContext) =>
            new CompositeComparer<MempoolTransaction>(
                new GasPriceTxComparerForProducer(blockPreparationContext, specProvider).ForMempool(),
                BlobTxPriorityComparer.Instance.ForMempool(),
                CompareTxByTimestamp.Instance.ForMempool(),
                CompareTxByPoolIndex.Instance,
                CompareTxByGasLimit.Instance.ForMempool());
    }
}
