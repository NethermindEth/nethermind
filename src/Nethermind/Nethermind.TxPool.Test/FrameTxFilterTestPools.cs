// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool.Collections;
using NSubstitute;

namespace Nethermind.TxPool.Test;

/// <summary>Pending-pool fixtures for the frame-transaction admission filters, which walk the pool a
/// displaced transaction would sit in.</summary>
public static class FrameTxFilterTestPools
{
    /// <summary>The real pool type for the shape, so the visitor's ascending-nonce exit is exercised as wired.</summary>
    public static TxDistinctSortedPool Pool(bool blobs, params Transaction[] pending)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new ReleaseSpec { IsEip1559Enabled = false });
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);

        IComparer<Transaction> comparer = new TransactionComparerProvider(specProvider, blockTree).GetDefaultComparer();
        TxDistinctSortedPool pool = blobs
            ? new BlobTxDistinctSortedPool(pending.Length + 1, comparer, LimboLogs.Instance)
            : new TxDistinctSortedPool(pending.Length + 1, comparer, LimboLogs.Instance);
        foreach (Transaction tx in pending)
        {
            pool.TryInsert(tx.Hash!, tx);
        }

        return pool;
    }
}
