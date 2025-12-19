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

namespace Nethermind.Xdc;

internal class CompareTxBySender(ISigner signer) : IComparer<Transaction>
{
    public int Compare(Transaction? newTx, Transaction? oldTx)
    {
        if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
        if (oldTx is null) return TxComparisonResult.KeepOld;
        if (newTx is null) return TxComparisonResult.TakeNew;

        if (!newTx.IsSpecialTransaction() && !oldTx.IsSpecialTransaction()) return TxComparisonResult.NotDecided;
        if (newTx.IsSpecialTransaction() && !oldTx.IsSpecialTransaction()) return TxComparisonResult.TakeNew;
        if (!newTx.IsSpecialTransaction() && oldTx.IsSpecialTransaction()) return TxComparisonResult.KeepOld;

        if(oldTx.SenderAddress == signer.Address) return TxComparisonResult.KeepOld;
        if(newTx.SenderAddress == signer.Address) return TxComparisonResult.TakeNew;

        return TxComparisonResult.NotDecided;
    }
}

internal class XdcTransactionComparerProvider(ISpecProvider specProvider, IBlockFinder blockFinder, ISigner signer)
    : ITransactionComparerProvider
{
    private readonly ITransactionComparerProvider defaultComparerProvider = new TransactionComparerProvider(specProvider, blockFinder);

    public IComparer<Transaction> GetDefaultComparer()
    {
        var defaultComparer = defaultComparerProvider.GetDefaultComparer();

        var signerFilter = new CompareTxBySender(signer);

        return signerFilter.ThenBy(defaultComparer);
    }

    public IComparer<Transaction> GetDefaultProducerComparer(BlockPreparationContext blockPreparationContext)
    {
        var defaultComparer = defaultComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        var signerFilter = new CompareTxBySender(signer);

        return signerFilter.ThenBy(defaultComparer);
    }
}
