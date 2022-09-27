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
    private StateTree _inputTree;

    [OneTimeSetUp]
    public void Setup()
    {
        _inputTree = TestItem.Tree.GetStateTree(null);
    }

    [Test]
    public void AccountRangeFetchVisitor()
    {
        var startHash = (new Keccak("0000000000000000000000000000000000000000000000000000000001113456")).Bytes;
        var limitHash = (new Keccak("0000000000000000000000000000000000000000000000000000000001123458")).Bytes;

        RangeQueryVisitor visitor = new(startHash, limitHash);
        VisitingOptions opt = new()
        {
            ExpectAccounts = false,
            KeepTrackOfAbsolutePath = true
        };
        _inputTree.Accept(visitor, _inputTree.RootHash, opt);
        byte[][] nodes = visitor.GetNodes();

        Assert.AreEqual(nodes.Length, 4);

        Rlp.Encode(TestItem.Tree.AccountsWithPaths[2].Account).Bytes.Should().BeEquivalentTo(nodes[0]);
        Rlp.Encode(TestItem.Tree.AccountsWithPaths[3].Account).Bytes.Should().BeEquivalentTo(nodes[1]);
        Rlp.Encode(TestItem.Tree.AccountsWithPaths[4].Account).Bytes.Should().BeEquivalentTo(nodes[2]);
        Rlp.Encode(TestItem.Tree.AccountsWithPaths[5].Account).Bytes.Should().BeEquivalentTo(nodes[3]);
    }

    [Test]
    public void StorageRangeFetchVisitor()
    {
        TrieStore store = new TrieStore(new MemDb(), LimboLogs.Instance);
        (StateTree inputStateTree, StorageTree inputStorageTree) = TestItem.Tree.GetTrees(store);

        RangeQueryVisitor visitor = new(Keccak.Zero.Bytes, Keccak.MaxValue.Bytes);
        VisitingOptions opt = new()
        {
            ExpectAccounts = false,
            KeepTrackOfAbsolutePath = true
        };
        inputStorageTree.Accept(visitor, inputStorageTree.RootHash, opt);
        byte[][] nodes = visitor.GetNodes();
        Assert.AreEqual(nodes.Length, 6);

        nodes[0].Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[0].SlotRlpValue);
        nodes[1].Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[1].SlotRlpValue);
        nodes[2].Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[2].SlotRlpValue);
        nodes[3].Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[3].SlotRlpValue);
        nodes[4].Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[4].SlotRlpValue);
        nodes[5].Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[5].SlotRlpValue);
    }
}
