// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using Org.BouncyCastle.Asn1.X509;

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

    [TestCase(false, 0, "000000000000000000003333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(false, 1, "002000000000000000013333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(false, 4, "002222000000000000043333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(false, 5, "002222200000000000053333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(false, 6, "012222220000000000063333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(false, 10, "0122222222220000000a3333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(false, 32, "012222222222222222203333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(true, 0, "0211111111111111111111111111111111111111111111111111111111111111110000000000000000003333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(true, 1, "0211111111111111111111111111111111111111111111111111111111111111112000000000000000013333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(true, 5, "0211111111111111111111111111111111111111111111111111111111111111112222200000000000053333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(true, 10, "02111111111111111111111111111111111111111111111111111111111111111122222222220000000a3333333333333333333333333333333333333333333333333333333333333333")]
    [TestCase(true, 32, "0211111111111111111111111111111111111111111111111111111111111111112222222222222222203333333333333333333333333333333333333333333333333333333333333333")]
    public void Test_HalfPathEncodng(bool hasAddress, int pathLength, string expectedKey)
    {
        if (_currentKeyScheme == INodeStorage.KeyScheme.Hash) return;

        Hash256? address = null;
        if (hasAddress)
        {
            address = new Hash256("1111111111111111111111111111111111111111111111111111111111111111");
        }

        TreePath path = new TreePath(new Hash256("2222222222222222222222222222222222222222222222222222222222222222"), 64);
        path.TruncateMut(pathLength);

        Hash256? hash = new Hash256("3333333333333333333333333333333333333333333333333333333333333333");

        NodeStorage.GetHalfPathNodeStoragePath(address, path, hash).ToHexString().Should().Be(expectedKey);
    }

    [TestCase(false, 3, ReadFlags.HintReadAhead)]
    [TestCase(false, 10, ReadFlags.HintReadAhead | ReadFlags.HintReadAhead2)]
    [TestCase(true, 3, ReadFlags.HintReadAhead | ReadFlags.HintReadAhead3)]
    public void Test_WhenReadaheadUseDifferentReadaheadOnDifferentSection(bool hasAddress, int pathLength, ReadFlags expectedReadFlags)
    {
        if (_currentKeyScheme == INodeStorage.KeyScheme.Hash) return;

        TestMemDb testDb = new TestMemDb();
        NodeStorage nodeStorage = new NodeStorage(testDb, _currentKeyScheme);

        Hash256? address = null;
        if (hasAddress)
        {
            address = new Hash256("1111111111111111111111111111111111111111111111111111111111111111");
        }

        TreePath path = new TreePath(new Hash256("2222222222222222222222222222222222222222222222222222222222222222"), 64);
        path.TruncateMut(pathLength);

        nodeStorage.Get(address, path, Keccak.Zero, ReadFlags.HintReadAhead);
        testDb.KeyWasReadWithFlags(NodeStorage.GetHalfPathNodeStoragePath(address, path, Keccak.Zero), expectedReadFlags);
    }
}
