// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.ScopeProvider;

[TestFixture]
public class FlatReadOnlyTrieStoreTests
{
    private IFlatDbManager _flatDbManager = null!;
    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp()
    {
        _flatDbManager = Substitute.For<IFlatDbManager>();
        _pool = new ResourcePool(new FlatDbConfig { CompactSize = 2 });
    }

    [Test]
    public void HasRoot_StateRootOnly_AlwaysTrue()
    {
        FlatReadOnlyTrieStore store = new(_flatDbManager);
        store.HasRoot(TestItem.KeccakA).Should().BeTrue();
    }

    [Test]
    public void HasRoot_WithBlockNumber_DelegatesToFlatDbManager()
    {
        _flatDbManager.HasStateForBlock(Arg.Any<StateId>()).Returns(true);
        FlatReadOnlyTrieStore store = new(_flatDbManager);

        store.HasRoot(TestItem.KeccakA, 42).Should().BeTrue();
        _flatDbManager.Received(1).HasStateForBlock(Arg.Any<StateId>());
    }

    [Test]
    public void Resolve_BeforeBeginScope_Throws()
    {
        FlatReadOnlyTrieStore store = new(_flatDbManager);
        Assert.That(() => store.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA),
            Throws.InvalidOperationException);
    }

    [Test]
    public void BeginScope_NullBundle_Throws()
    {
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns((ReadOnlySnapshotBundle)null!);
        FlatReadOnlyTrieStore store = new(_flatDbManager);

        Assert.That(() => store.BeginScope(Build.A.BlockHeader.TestObject), Throws.InvalidOperationException);
    }

    [Test]
    public void BeginScope_EnablesStateAndStorageNodeLookup()
    {
        TreePath path = TreePath.FromHexString("12");
        TrieNode stateNode = new(NodeType.Leaf, [0xc1, 0x01]);
        Hash256 storageAddress = TestItem.KeccakA;
        TrieNode storageNode = new(NodeType.Leaf, [0xc1, 0x02]);

        ReadOnlySnapshotBundle bundle = FlatTestHelpers.MakeBundle(_pool, c =>
        {
            c.StateNodes[new HashedKey<TreePath>(path)] = stateNode;
            c.StorageNodes[new HashedKey<(Hash256, TreePath)>((storageAddress, path))] = storageNode;
        });
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(bundle);

        FlatReadOnlyTrieStore store = new(_flatDbManager);
        using IDisposable scope = store.BeginScope(Build.A.BlockHeader.TestObject);

        store.FindCachedOrUnknown(null, path, Keccak.Zero).Should().BeSameAs(stateNode);
        store.FindCachedOrUnknown(storageAddress, path, Keccak.Zero).Should().BeSameAs(storageNode);
    }

    [Test]
    public void Scope_DisposesBundle_AndPreventsResolve()
    {
        ReadOnlySnapshotBundle bundle = FlatTestHelpers.MakeBundle(_pool);
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(bundle);

        FlatReadOnlyTrieStore store = new(_flatDbManager);
        IDisposable scope = store.BeginScope(Build.A.BlockHeader.TestObject);
        scope.Dispose();

        Assert.That(() => store.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA),
            Throws.InvalidOperationException);
    }

    [Test]
    public void BeginCommit_ReturnsNullCommitter_NoOps()
    {
        FlatReadOnlyTrieStore store = new(_flatDbManager);

        store.BeginBlockCommit(1).Should().NotBeNull();
        store.BeginCommit(null, null, WriteFlags.None).Should().NotBeNull();
    }

    [Test]
    public void GetTrieStore_ReturnsScopedAdapter()
    {
        FlatReadOnlyTrieStore store = new(_flatDbManager);
        store.GetTrieStore(TestItem.KeccakA).Should().NotBeNull();
        store.GetTrieStore(null).Should().NotBeNull();
    }

    [Test]
    public void Dispose_BeforeBeginScope_DoesNothing()
    {
        FlatReadOnlyTrieStore store = new(_flatDbManager);
        Assert.DoesNotThrow(() => store.Dispose());
    }
}
