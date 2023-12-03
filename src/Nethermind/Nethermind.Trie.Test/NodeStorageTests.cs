// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture(INodeStorage.KeyScheme.Hash)]
[TestFixture(INodeStorage.KeyScheme.HalfPath)]
public class NodeStorageTests
{
    private readonly INodeStorage.KeyScheme _currentKeyScheme;

    public NodeStorageTests(INodeStorage.KeyScheme currentKeyScheme)
    {
        _currentKeyScheme = currentKeyScheme;
    }

    [Test]
    public void Should_StoreAndRead()
    {
        TestMemDb testDb = new TestMemDb();
        NodeStorage nodeStorage = new NodeStorage(testDb, _currentKeyScheme);

        nodeStorage.KeyExists(null, TreePath.Empty, TestItem.KeccakA).Should().BeFalse();
        nodeStorage.Set(null, TreePath.Empty, TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        nodeStorage.Get(null, TreePath.Empty, TestItem.KeccakA).Should().BeEquivalentTo(TestItem.KeccakA.BytesToArray());
        nodeStorage.KeyExists(null, TreePath.Empty, TestItem.KeccakA).Should().BeTrue();

        if (_currentKeyScheme == INodeStorage.KeyScheme.Hash)
        {
            testDb[TestItem.KeccakA.Bytes].Should().NotBeNull();
        }
        else if (_currentKeyScheme == INodeStorage.KeyScheme.HalfPath)
        {
            testDb[NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, TestItem.KeccakA)].Should().NotBeNull();
        }
    }

    [Test]
    public void Should_StoreAndRead_WithStorage()
    {
        TestMemDb testDb = new TestMemDb();
        NodeStorage nodeStorage = new NodeStorage(testDb, _currentKeyScheme);

        nodeStorage.KeyExists(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA).Should().BeFalse();
        nodeStorage.Set(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        nodeStorage.Get(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA).Should().BeEquivalentTo(TestItem.KeccakA.BytesToArray());
        nodeStorage.KeyExists(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA).Should().BeTrue();

        if (_currentKeyScheme == INodeStorage.KeyScheme.Hash)
        {
            testDb[TestItem.KeccakA.Bytes].Should().NotBeNull();
        }
        else if (_currentKeyScheme == INodeStorage.KeyScheme.HalfPath)
        {
            testDb[NodeStorage.GetHalfPathNodeStoragePath(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA)].Should().NotBeNull();
        }
    }

    [Test]
    public void When_KeyNotExist_Should_TryBothKeyType()
    {
        TestMemDb testDb = new TestMemDb();
        NodeStorage nodeStorage = new NodeStorage(testDb, _currentKeyScheme);

        nodeStorage.Get(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA).Should().BeNull();

        testDb.KeyWasRead(TestItem.KeccakA.Bytes.ToArray());
        testDb.KeyWasRead(NodeStorage.GetHalfPathNodeStoragePath(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA));
    }

    [Test]
    public void When_EntryOfDifferentScheme_Should_StillBeAbleToRead()
    {
        TestMemDb testDb = new TestMemDb();
        NodeStorage nodeStorage = new NodeStorage(testDb, _currentKeyScheme);

        if (_currentKeyScheme == INodeStorage.KeyScheme.Hash)
        {
            testDb[NodeStorage.GetHalfPathNodeStoragePath(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA)] =
                TestItem.KeccakA.BytesToArray();
        }
        else
        {
            testDb[TestItem.KeccakA.Bytes] = TestItem.KeccakA.BytesToArray();
        }

        nodeStorage.Get(TestItem.KeccakB, TreePath.Empty, TestItem.KeccakA).Should().BeEquivalentTo(TestItem.KeccakA.BytesToArray());
    }
}
