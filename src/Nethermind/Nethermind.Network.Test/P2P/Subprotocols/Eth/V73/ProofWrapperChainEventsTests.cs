// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

public class ProofWrapperChainEventsTests
{
    [Test]
    public void Records_inclusion_and_pool_eviction_of_dependency_bearing_transactions_only_until_disposed()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ITxPool txPool = Substitute.For<ITxPool>();
        ProofWrapperEventFeed feed = new(8);
        Transaction bearing = FrameTransaction(3);
        Transaction plain = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;
        Block block = Build.A.Block.WithNumber(7).WithTransactions(bearing, plain).TestObject;

        using (new ProofWrapperChainEvents(feed, blockTree, txPool))
        {
            blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
            txPool.EvictedPending += Raise.Event<EventHandler<TxEventArgs>>(txPool, new TxEventArgs(bearing));
            txPool.EvictedPending += Raise.Event<EventHandler<TxEventArgs>>(txPool, new TxEventArgs(plain));
        }
        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        ProofWrapperEvent[] events = feed.Read(0, 10).Events;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events.Select(e => e.Type), Is.EqualTo(new[] { ProofWrapperEventTypes.Included, ProofWrapperEventTypes.PoolEvicted }));
            Assert.That(events[0].Block, Is.EqualTo(7));
            Assert.That(events[0].BlockHash, Is.EqualTo(block.Hash));
            Assert.That(events.Select(e => e.Transactions!.Single()), Is.All.EqualTo(bearing.Hash));
        }
    }
}
