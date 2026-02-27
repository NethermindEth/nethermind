// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

/// <summary>
/// Tests for TrieLeafIterator which provides in-order leaf iteration.
/// </summary>
[TestFixture]
public class TrieLeafIteratorTests
{
    private MemDb _db = null!;
    private RawScopedTrieStore _trieStore = null!;
    private StateTree _stateTree = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new MemDb();
        _trieStore = new RawScopedTrieStore(_db);
        _stateTree = new StateTree(_trieStore, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public void EmptyTrie_ReturnsNoLeaves()
    {
        TrieLeafIterator iterator = new TrieLeafIterator(_trieStore, Keccak.EmptyTreeHash);

        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void SingleAccount_ReturnsOneLeaf()
    {
        _stateTree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        _stateTree.Commit();

        TrieLeafIterator iterator = new TrieLeafIterator(_trieStore, _stateTree.RootHash);

        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
            Assert.That(iterator.CurrentLeaf, Is.Not.Null);
            Assert.That(iterator.CurrentLeaf!.IsLeaf, Is.True);
        }

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void MultipleAccounts_ReturnsAllLeaves()
    {
        _stateTree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        _stateTree.Set(TestItem.AddressB, TestItem.GenerateIndexedAccount(1));
        _stateTree.Set(TestItem.AddressC, TestItem.GenerateIndexedAccount(2));
        _stateTree.Commit();

        TrieLeafIterator iterator = new TrieLeafIterator(_trieStore, _stateTree.RootHash);

        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
            Assert.That(iterator.CurrentLeaf, Is.Not.Null);
            Assert.That(iterator.CurrentLeaf!.IsLeaf, Is.True);
        }

        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public void Iterator_ReturnsLeavesInSortedOrder()
    {
        // Add accounts and get their hashed paths
        _stateTree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        _stateTree.Set(TestItem.AddressB, TestItem.GenerateIndexedAccount(1));
        _stateTree.Set(TestItem.AddressC, TestItem.GenerateIndexedAccount(2));
        _stateTree.Set(TestItem.AddressD, TestItem.GenerateIndexedAccount(3));
        _stateTree.Set(TestItem.AddressE, TestItem.GenerateIndexedAccount(4));
        _stateTree.Commit();

        TrieLeafIterator iterator = new TrieLeafIterator(_trieStore, _stateTree.RootHash);

        // Store copies of paths to avoid any ref struct sharing issues
        List<byte[]> paths = [];
        while (iterator.MoveNext())
        {
            // Copy the path bytes to a new array
            paths.Add(iterator.CurrentPath.Path.Bytes.ToArray());
        }

        Assert.That(paths.Count, Is.EqualTo(5));

        // Verify paths are in ascending order using byte comparison
        for (int i = 1; i < paths.Count; i++)
        {
            int cmp = paths[i - 1].AsSpan().SequenceCompareTo(paths[i]);
            Assert.That(cmp, Is.LessThan(0),
                $"Paths should be in ascending order. Path[{i - 1}] should be < Path[{i}]");
        }
    }

    [Test]
    public void NullRoot_ReturnsNoLeaves()
    {
        TrieLeafIterator iterator = new TrieLeafIterator(_trieStore, null);

        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void CurrentPath_MatchesKeccakOfAddress()
    {
        Address address = TestItem.AddressA;
        _stateTree.Set(address, TestItem.GenerateIndexedAccount(0));
        _stateTree.Commit();

        // Compute expected path
        Hash256 expectedPath = Keccak.Compute(address.Bytes);

        TrieLeafIterator iterator = new TrieLeafIterator(_trieStore, _stateTree.RootHash);

        Assert.That(iterator.MoveNext(), Is.True);

        // Get actual path from iterator
        byte[] actualPathBytes = iterator.CurrentPath.Path.Bytes.ToArray();
        byte[] expectedPathBytes = expectedPath.Bytes.ToArray();

        Assert.That(actualPathBytes, Is.EqualTo(expectedPathBytes),
            $"Expected: {expectedPath}, Actual: {iterator.CurrentPath.Path}");
    }
}
