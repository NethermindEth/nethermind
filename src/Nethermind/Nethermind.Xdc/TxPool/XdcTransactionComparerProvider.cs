// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.TxPool.Comparison;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using System.Collections.Generic;

namespace Nethermind.Xdc.TxPool;

internal class CompareTxBySender(IXdcReleaseSpec spec) : IComparer<Transaction>
{
    public int Compare(Transaction? newTx, Transaction? oldTx)
    {
        if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
        if (oldTx is null) return TxComparisonResult.KeepOld;
        if (newTx is null) return TxComparisonResult.TakeNew;

        bool isNewTxSpecial = newTx.IsSpecialTransaction(spec);
        bool isOldTxSpecial = oldTx.IsSpecialTransaction(spec);

        if (!isNewTxSpecial && !isOldTxSpecial) return TxComparisonResult.NotDecided;
        if (isNewTxSpecial && !isOldTxSpecial) return TxComparisonResult.TakeNew;
        if (!isNewTxSpecial && isOldTxSpecial) return TxComparisonResult.KeepOld;

        return TxComparisonResult.NotDecided;
    }
}

internal class XdcTransactionComparerProvider(ISpecProvider specProvider, IBlockFinder blockFinder)
    : ITransactionComparerProvider
{
    private readonly ITransactionComparerProvider defaultComparerProvider = new TransactionComparerProvider(specProvider, blockFinder);

    public IComparer<Transaction> GetDefaultComparer()
    {
        IComparer<Transaction> defaultComparer = defaultComparerProvider.GetDefaultComparer();

        IXdcReleaseSpec finalSpec = specProvider.GetXdcSpec(ulong.MaxValue - 1);
        CompareTxBySender signerFilter = new(finalSpec);

        return signerFilter.ThenBy(defaultComparer);
    }

    public IComparer<Transaction> GetDefaultProducerComparer(BlockPreparationContext blockPreparationContext)
    {
        IComparer<Transaction> defaultComparer = defaultComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        IXdcReleaseSpec currentSpec = specProvider.GetXdcSpec(blockPreparationContext.BlockNumber);
        CompareTxBySender signerFilter = new(currentSpec);

        return signerFilter.ThenBy(defaultComparer);
    }
}
