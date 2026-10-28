// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.NodeBuffer;
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
    private FlatReadOnlyTrieStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _flatDbManager = Substitute.For<IFlatDbManager>();
        _pool = new ResourcePool(new FlatDbConfig { CompactSize = 2 });
        _store = new FlatReadOnlyTrieStore(_flatDbManager);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    [Test]
    public void HasRoot_StateRootOnly_AlwaysTrue() =>
        Assert.That(_store.HasRoot(TestItem.KeccakA), Is.True);

    [Test]
    public void HasRoot_WithBlockNumber_DelegatesToFlatDbManager()
    {
        _flatDbManager.HasStateForBlock(Arg.Any<StateId>()).Returns(true);

        Assert.That(_store.HasRoot(TestItem.KeccakA, 42), Is.True);
        _flatDbManager.Received(1).HasStateForBlock(Arg.Any<StateId>());
    }

    [Test]
    public void Resolve_BeforeBeginScope_Throws() =>
        Assert.That(() => _store.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA),
            Throws.InvalidOperationException);

    [Test]
    public void BeginScope_NullBundle_Throws()
    {
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns((ReadOnlySnapshotBundle)null!);

        Assert.That(() => _store.BeginScope(Build.A.BlockHeader.TestObject), Throws.InvalidOperationException);
    }

    [Test]
    public void BeginScope_EnablesStateAndStorageNodeLookup()
    {
        TreePath path = TreePath.FromHexString("12");
        Hash256 storageAddress = TestItem.KeccakA;
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();

        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(FlatTestHelpers.MakeBundle(_pool, buffer, c =>
        {
            c.SetStateNodeRlp(buffer, path, FlatTestHelpers.LeafRlpA);
            c.SetStorageNodeRlp(buffer, storageAddress, path, FlatTestHelpers.LeafRlpB);
        }));

        using IDisposable scope = _store.BeginScope(Build.A.BlockHeader.TestObject);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.FindCachedOrUnknown(null, path, Keccak.Compute(Bytes.FromHexString(FlatTestHelpers.LeafRlpA))).FullRlp.ToArray(), Is.EqualTo(Bytes.FromHexString(FlatTestHelpers.LeafRlpA)));
            Assert.That(_store.FindCachedOrUnknown(storageAddress, path, Keccak.Compute(Bytes.FromHexString(FlatTestHelpers.LeafRlpB))).FullRlp.ToArray(), Is.EqualTo(Bytes.FromHexString(FlatTestHelpers.LeafRlpB)));
        }
    }

    [Test]
    public void Scope_DisposesBundle_AndPreventsResolve()
    {
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(FlatTestHelpers.MakeBundle(_pool));

        IDisposable scope = _store.BeginScope(Build.A.BlockHeader.TestObject);
        scope.Dispose();

        Assert.That(() => _store.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA),
            Throws.InvalidOperationException);
    }

    [Test]
    public void BeginScope_WhileScopeOpen_Throws_ButReopensAfterDispose()
    {
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(_ => FlatTestHelpers.MakeBundle(_pool));
        BlockHeader header = Build.A.BlockHeader.TestObject;

        IDisposable scope = _store.BeginScope(header);
        Assert.That(() => _store.BeginScope(header), Throws.InvalidOperationException);
        _flatDbManager.Received(1).GatherReadOnlySnapshotBundle(Arg.Any<StateId>());

        scope.Dispose();
        Assert.That(() => _store.BeginScope(header), Throws.Nothing);
    }

    [Test]
    public void BeginCommit_ReturnsNullCommitter_NoOps()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.BeginBlockCommit(1), Is.Not.Null);
            Assert.That(_store.BeginCommit(null, null, WriteFlags.None), Is.Not.Null);
        }
    }

    [Test]
    public void GetTrieStore_ReturnsScopedAdapter()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.GetTrieStore(TestItem.KeccakA), Is.Not.Null);
            Assert.That(_store.GetTrieStore(null), Is.Not.Null);
        }
    }

    [Test]
    public void Dispose_BeforeBeginScope_DoesNothing() =>
        Assert.DoesNotThrow(() => _store.Dispose());
}
