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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class PatriciaTreeTests
    {
        [Test]
        public void Create_commit_change_balance_get()
        {
            Account account = new Account(1);
            StateTree stateTree = new StateTree();
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Account accountRestored = stateTree.Get(TestItem.AddressA);
            Assert.AreEqual((UInt256) 2, accountRestored.Balance);
        }

        [Test]
        public void Create_create_commit_change_balance_get()
        {
            Account account = new Account(1);
            StateTree stateTree = new StateTree();
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Set(TestItem.AddressB, account);
            stateTree.Commit(0);

            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Account accountRestored = stateTree.Get(TestItem.AddressA);
            Assert.AreEqual((UInt256) 2, accountRestored.Balance);
        }

        [Test]
        public void Create_commit_reset_change_balance_get()
        {
            MemDb db = new MemDb();
            Account account = new Account(1);
            StateTree stateTree = new StateTree(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Keccak rootHash = stateTree.RootHash;
            stateTree.RootHash = null;

            stateTree.RootHash = rootHash;
            stateTree.Get(TestItem.AddressA);
            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Assert.AreEqual(2, db.Keys.Count);
        }
    }
}
