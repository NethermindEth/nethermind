// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class TreeSyncSavedNodeCacheTests() : StateSyncFeedTestsBase()
{
    [Test]
    public async Task New_state_root_checks_the_store_again_for_a_node_seen_at_an_earlier_root()
    {
        Hash256 firstRoot = TestItem.KeccakA;
        Hash256 secondRoot = TestItem.KeccakB;
        ITreeSyncStore store = Substitute.For<ITreeSyncStore>();
        store.NodeExists(Arg.Any<Hash256?>(), Arg.Any<TreePath>(), Arg.Any<ValueHash256>()).Returns(true);
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        pivot.GetPivotHeader().Returns(
            Build.A.BlockHeader.WithNumber(1).WithStateRoot(firstRoot).TestObject,
            Build.A.BlockHeader.WithNumber(2).WithStateRoot(secondRoot).TestObject,
            Build.A.BlockHeader.WithNumber(3).WithStateRoot(firstRoot).TestObject);

        await using IContainer container = BuildTestContainerBuilder(new RemoteDbContext(_logManager))
            .AddSingleton<ITreeSyncStore>(store)
            .AddSingleton<IStateSyncPivot>(pivot)
            .Build();
        TreeSync treeSync = container.Resolve<TreeSync>();

        treeSync.ResetStateRootToBestSuggested();
        treeSync.ResetStateRootToBestSuggested();
        treeSync.ResetStateRootToBestSuggested();

        store.Received(2).NodeExists(Arg.Any<Hash256?>(), Arg.Any<TreePath>(), Arg.Is(TestItem.KeccakA.ValueHash256));
    }
}
