//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Blockchain.TxPools.Collections;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.TxPools
{
    [TestFixture]
    public class SortedPoolTests
    {
        private const int Capacity = 16;

        private readonly SortedPool<Keccak, Transaction> _sortedPool = new SortedPool<Keccak, Transaction>(Capacity, (t1, t2) => t1.GasPrice.CompareTo(t2.GasPrice));

        private Transaction[] _transactions = new Transaction[Capacity * 8];
        
        [SetUp]
        public void Setup()
        {
            for (int i = 0; i < _transactions.Length; i++)
            {
                UInt256.Create(out UInt256 gasPrice, i);
                _transactions[i] = Build.A.Transaction.WithGasPrice(gasPrice).TestObject;
            }
        }

        [Test]
        public void Beyond_capacity()
        {
            for (int i = 0; i < _transactions.Length; i++)
            {
                var tx = _transactions[^(i + 1)];
                tx.Hash = tx.CalculateHash();
                _sortedPool.TryInsert(tx.Hash, tx);
                Assert.AreEqual(i > 15 ? null : tx, _sortedPool.TryGetValue(tx.Hash, out Transaction txOther) ? txOther : null);
                Assert.AreEqual(Math.Min(16, i + 1), _sortedPool.Count);
            }

            Assert.AreEqual(Capacity, _sortedPool.GetSnapshot().Length);
            
            for (int i = 0; i < Capacity; i++)
            {
                Transaction tx = _sortedPool.TakeFirst();
                UInt256.Create(out UInt256 gasPrice, _transactions.Length - i - 1);
                Assert.AreEqual(Capacity - i - 1, _sortedPool.Count);
                Assert.AreEqual(gasPrice, tx.GasPrice);
            }
        }
        
        [Test]
        public void Beyond_capacity_ordered()
        {
            for (int i = 0; i < _transactions.Length; i++)
            {
                var tx = _transactions[i];
                tx.Hash = tx.CalculateHash();
                _sortedPool.TryInsert(tx.Hash, tx);
                Assert.AreEqual(Math.Min(16, i + 1), _sortedPool.Count);
            }

            for (int i = 0; i < Capacity; i++)
            {
                Transaction tx = _sortedPool.TakeFirst();
                UInt256.Create(out UInt256 gasPrice, _transactions.Length - i - 1);
                Assert.AreEqual(Capacity - i - 1, _sortedPool.Count);
                Assert.AreEqual(gasPrice, tx.GasPrice);
            }
        }
    }
}