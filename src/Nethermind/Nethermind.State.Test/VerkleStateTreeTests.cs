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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class VerkleStateTreeTests
    {
        private readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;

        [SetUp]
        public void Setup()
        {
            Trie.Metrics.TreeNodeHashCalculations = 0;
            Trie.Metrics.TreeNodeRlpDecodings = 0;
            Trie.Metrics.TreeNodeRlpEncodings = 0;
        }
        
        
        [Test]
        public void Set_Get_Account()
        {
            VerkleStateTree tree = new(LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            
            Account account = tree.Get(TestItem.AddressA);
            
            Assert.AreEqual(_account0.Balance, account.Balance);
            Assert.AreEqual(_account0.Nonce, account.Nonce);
            Assert.AreEqual(_account0.CodeHash, account.CodeHash);
            Assert.AreEqual(_account0.CodeSize, account.CodeSize);
            
        }
        
    }
}

