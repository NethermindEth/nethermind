// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.History.Test;

internal static class HistoryTestHelpers
{
    internal static Block CreateProcessedOrphan(BasicTestBlockchain chain, ulong parentNumber, Address address)
    {
        Block parent = chain.BlockTree.FindBlock(parentNumber, BlockTreeLookupOptions.None)!;
        LogEntry log = Build.A.LogEntry.WithAddress(address).WithTopics(TestItem.KeccakF).TestObject;
        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        Block sideBlock = Build.A.Block
            .WithParent(parent)
            .WithTransactions(tx)
            .WithReceiptsRoot(TestItem.KeccakA)
            .WithBloom(new Bloom([log]))
            .WithExtraData([1])
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.SuggestBlock(sideBlock, BlockTreeSuggestOptions.ForceDontSetAsMain), Is.EqualTo(AddBlockResult.Added));
            Assert.That(chain.BlockTree.GetInfo(sideBlock.Number, sideBlock.Hash!).Info, Is.Not.Null,
                "the side block must have chain-level metadata before it is marked processed");
        }
        chain.BlockTree.MarkChainAsProcessed([sideBlock]);
        chain.ReceiptStorage.Insert(sideBlock, [Build.A.Receipt
            .WithLogs(log)
            .WithTransactionHash(tx.Hash)
            .WithBlockHash(sideBlock.Hash)
            .WithBlockNumber(sideBlock.Number)
            .TestObject], ensureCanonical: false);

        return sideBlock;
    }
}
