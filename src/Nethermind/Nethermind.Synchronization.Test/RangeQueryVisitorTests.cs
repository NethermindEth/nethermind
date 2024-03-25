//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class RangeQueryVisitorTests
{
    private StateTree _inputTree = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _inputTree = TestItem.Tree.GetStateTree();
    }

    [Test]
    public void AccountRangeFetchVisitor()
    {
        var startHash = new Hash256("0000000000000000000000000000000000000000000000000000000001113456");
        var limitHash = new Hash256("0000000000000000000000000000000000000000000000000000000001123458");

        RlpCollector leafCollector = new();
        using RangeQueryVisitor visitor = new(startHash, limitHash, leafCollector);
        _inputTree.Accept(visitor, _inputTree.RootHash, CreateVisitingOptions());

        leafCollector.Leafs.Count.Should().Be(4);

        int k = 0;
        leafCollector.Leafs.Should().AllSatisfy(pair =>
            Rlp.Encode(TestItem.Tree.AccountsWithPaths[k++ + 2].Account).Bytes.Should().BeEquivalentTo(pair.Item2)
        );
    }

    [Test]
    public void AccountRangeFetchWithSparserTree()
    {
        StateTree tree = new StateTree();
        tree.Set(new Hash256("0100000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        tree.Set(new Hash256("0200000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        tree.Set(new Hash256("0300000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        tree.Set(new Hash256("0400000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        tree.Set(new Hash256("0500000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        tree.UpdateRootHash();

        var startHash = new Hash256("0150000000000000000000000000000000000000000000000000000000000000");
        var limitHash = new Hash256("0350000000000000000000000000000000000000000000000000000000000000");

        RlpCollector leafCollector = new();
        using RangeQueryVisitor visitor = new(startHash, limitHash, leafCollector);
        tree.Accept(visitor, tree.RootHash, CreateVisitingOptions());

        Dictionary<ValueHash256, byte[]?> nodes = leafCollector.Leafs.ToDictionary((it) => it.Item1, (it) => it.Item2);
        nodes.Count.Should().Be(3);

        nodes.ContainsKey(new Hash256("0200000000000000000000000000000000000000000000000000000000000000")).Should().BeTrue();
        nodes.ContainsKey(new Hash256("0300000000000000000000000000000000000000000000000000000000000000")).Should().BeTrue();
        nodes.ContainsKey(new Hash256("0400000000000000000000000000000000000000000000000000000000000000")).Should().BeTrue();
    }

    [Test]
    public void AccountRangeFetch_AfterTree()
    {
        StateTree tree = new StateTree();
        tree.Set(new Hash256("0400000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        tree.Set(new Hash256("0500000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        tree.UpdateRootHash();

        var startHash = new Hash256("0510000000000000000000000000000000000000000000000000000000000000");
        var limitHash = new Hash256("0600000000000000000000000000000000000000000000000000000000000000");

        RlpCollector leafCollector = new();
        using RangeQueryVisitor visitor = new(startHash, limitHash, leafCollector);
        tree.Accept(visitor, tree.RootHash, CreateVisitingOptions());

        leafCollector.Leafs.Count.Should().Be(0);
        Action act = () => visitor.GetProofs();
        act.Should().NotThrow();
    }

    private static VisitingOptions CreateVisitingOptions() => new() { ExpectAccounts = false };

    [Test]
    public void RangeFetchPartialLimit()
    {
        var stateTree = new StateTree();
        stateTree.Set(new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Set(new Hash256("0x1000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Set(new Hash256("0x2000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Set(new Hash256("0x3000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Set(new Hash256("0x4000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Set(new Hash256("0x5000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Set(new Hash256("0x6000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Set(new Hash256("0x7000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Set(new Hash256("0x8000000000000000000000000000000000000000000000000000000000000000"), TestItem.GenerateRandomAccount());
        stateTree.Commit(0);

        var startHash = new Hash256("0x3000000000000000000000000000000000000000000000000000000000000000");
        var limitHash = new Hash256("0x4500000000000000000000000000000000000000000000000000000000000000");

        RlpCollector leafCollector = new();
        using RangeQueryVisitor visitor = new(startHash, limitHash, leafCollector);
        stateTree.Accept(visitor, stateTree.RootHash, CreateVisitingOptions());
        leafCollector.Leafs.Count.Should().Be(3);
    }

    [Test]
    public void StorageRangeFetchVisitor()
    {
        TrieStore store = new TrieStore(new MemDb(), LimboLogs.Instance);
        (StateTree inputStateTree, StorageTree _, Hash256 account) = TestItem.Tree.GetTrees(store);

        RlpCollector leafCollector = new();
        using RangeQueryVisitor visitor = new(Keccak.Zero, Keccak.MaxValue, leafCollector);
        inputStateTree.Accept(visitor, inputStateTree.RootHash, CreateVisitingOptions(), storageAddr: account);
        Dictionary<ValueHash256, byte[]?> nodes = leafCollector.Leafs.ToDictionary((it) => it.Item1, (it) => it.Item2);
        nodes.Count.Should().Be(6);

        int k = 0;
        nodes.Should().AllSatisfy(pair => pair.Value.Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[k++ + 0].SlotRlpValue));
    }

    public class RlpCollector : RangeQueryVisitor.ILeafValueCollector
    {
        public ArrayPoolList<(ValueHash256, byte[]?)> Leafs { get; } = new(0);

        public int Collect(in ValueHash256 path, CappedArray<byte> value)
        {
            Leafs.Add((path, value.ToArray()));
            return 32 + Rlp.LengthOfByteString(value.Length, 0);
        }
    }
}
