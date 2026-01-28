// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool.Comparison;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.TxPool;

internal class CompareTxBySender(IXdcReleaseSpec spec) : IComparer<Transaction>
{
    public CompareTxBySender(ISpecProvider specProvider)
        : this((IXdcReleaseSpec)specProvider.GetFinalSpec())
    {
    }

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
        var defaultComparer = defaultComparerProvider.GetDefaultComparer();

        var signerFilter = new CompareTxBySender(specProvider);

        return signerFilter.ThenBy(defaultComparer);
    }

    public IComparer<Transaction> GetDefaultProducerComparer(BlockPreparationContext blockPreparationContext)
    {
        var defaultComparer = defaultComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        var currentSpec = specProvider.GetXdcSpec(blockPreparationContext.BlockNumber);

        var signerFilter = new CompareTxBySender(currentSpec);

        return signerFilter.ThenBy(defaultComparer);
    }
}
