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
        _inputTree = TestItem.Tree.GetStateTree(null);
    }

    [Test]
    public void AccountRangeFetchVisitor()
    {
        var startHash = new Hash256("0000000000000000000000000000000000000000000000000000000001113456");
        var limitHash = new Hash256("0000000000000000000000000000000000000000000000000000000001123458");

        using RangeQueryVisitor visitor = new(startHash, limitHash, false);
        VisitingOptions opt = new()
        {
            ExpectAccounts = false,
            MaxDegreeOfParallelism = 1,
        };
        _inputTree.Accept(visitor, _inputTree.RootHash, opt);
        (IDictionary<ValueHash256, byte[]> nodes, long _) = visitor.GetNodesAndSize();

        nodes.Count.Should().Be(4);

        int k = 0;
        foreach (KeyValuePair<ValueHash256, byte[]> pair in nodes)
        {
            Rlp.Encode(TestItem.Tree.AccountsWithPaths[k + 2].Account).Bytes.Should().BeEquivalentTo(pair.Value);
            k += 1;
        }
    }

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

        using RangeQueryVisitor visitor = new(startHash, limitHash, false);
        VisitingOptions opt = new()
        {
            ExpectAccounts = false,
            MaxDegreeOfParallelism = 1,
        };
        stateTree.Accept(visitor, stateTree.RootHash, opt);
        (IDictionary<ValueHash256, byte[]> nodes, long _) = visitor.GetNodesAndSize();
        nodes.Count.Should().Be(2);
    }

    [Test]
    public void StorageRangeFetchVisitor()
    {
        TrieStore store = new TrieStore(new MemDb(), LimboLogs.Instance);
        (StateTree inputStateTree, StorageTree inputStorageTree, Hash256 account) = TestItem.Tree.GetTrees(store);

        using RangeQueryVisitor visitor = new(Keccak.Zero, Keccak.MaxValue, false);
        VisitingOptions opt = new()
        {
            ExpectAccounts = false,
        };
        inputStateTree.Accept(visitor, inputStateTree.RootHash, opt, storageAddr: account);
        (var nodes, long _) = visitor.GetNodesAndSize();
        nodes.Count.Should().Be(6);

        int k = 0;
        foreach (KeyValuePair<ValueHash256, byte[]> pair in nodes)
        {
            pair.Value.Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[k + 0].SlotRlpValue);
            k += 1;
        }
    }
}
