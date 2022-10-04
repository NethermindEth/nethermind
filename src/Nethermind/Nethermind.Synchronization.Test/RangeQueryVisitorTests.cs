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

        RangeQueryVisitor visitor = new(startHash, limitHash, false);
        VisitingOptions opt = new()
        {
            ExpectAccounts = false,
            KeepTrackOfAbsolutePath = true
        };
        _inputTree.Accept(visitor, _inputTree.RootHash, opt);
        var nodes = visitor.GetNodes();

        Assert.AreEqual(nodes.Count, 4);

        int k = 0;
        foreach (KeyValuePair<byte[], byte[]> pair in nodes)
        {
            Rlp.Encode(TestItem.Tree.AccountsWithPaths[k+2].Account).Bytes.Should().BeEquivalentTo(pair.Value);
            k += 1;
        }
    }

    [Test]
    public void StorageRangeFetchVisitor()
    {
        TrieStore store = new TrieStore(new MemDb(), LimboLogs.Instance);
        (StateTree inputStateTree, StorageTree inputStorageTree) = TestItem.Tree.GetTrees(store);

        RangeQueryVisitor visitor = new(Keccak.Zero.Bytes, Keccak.MaxValue.Bytes, false);
        VisitingOptions opt = new()
        {
            ExpectAccounts = false,
            KeepTrackOfAbsolutePath = true
        };
        inputStorageTree.Accept(visitor, inputStorageTree.RootHash, opt);
        var nodes = visitor.GetNodes();
        Assert.AreEqual(nodes.Count, 6);

        int k = 0;
        foreach (KeyValuePair<byte[], byte[]> pair in nodes)
        {
            pair.Value.Should().BeEquivalentTo(TestItem.Tree.SlotsWithPaths[k+0].SlotRlpValue);
            k += 1;
        }
    }
}
