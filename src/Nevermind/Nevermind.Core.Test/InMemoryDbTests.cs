/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nevermind.Core.Crypto;
using Nevermind.Store;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class InMemoryDbTests
    {
        private readonly Keccak _hash1 = Keccak.Compute("1");
        private Keccak _hash2 = Keccak.Compute("1");
        private Keccak _hash3 = Keccak.Compute("1");

        private byte[] _bytes1 = new byte[] {1};
        private byte[] _bytes2 = new byte[] {2};
        private byte[] _bytes3 = new byte[] {3};

        [Test]
        public void Set_get()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            inMemoryDb[_hash1] = _bytes1;
            byte[] getResult = inMemoryDb[_hash1];
            Assert.AreEqual(_bytes1, getResult);
        }
        
        [Test]
        public void Double_set_get()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            inMemoryDb[_hash1] = _bytes1;
            inMemoryDb[_hash1] = _bytes2;
            byte[] getResult = inMemoryDb[_hash1];
            Assert.AreEqual(_bytes2, getResult);
        }
        
        [Test]
        public void Set_delete_get()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            inMemoryDb[_hash1] = _bytes1;
            inMemoryDb.Delete(_hash1);
            byte[] getResult = inMemoryDb[_hash1];
            Assert.AreEqual(null, getResult);
        }
        
        [Test]
        public void Initial_take_snapshot()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            Assert.AreEqual(-1, inMemoryDb.TakeSnapshot());
        }
        
        [Test]
        public void Set_take_snapshot()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            inMemoryDb[_hash1] = _bytes1;
            Assert.AreEqual(0, inMemoryDb.TakeSnapshot());
        }
        
        [Test]
        public void Set_restore_get()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            inMemoryDb[_hash1] = _bytes1;
            inMemoryDb.Restore(-1);
            byte[] getResult = inMemoryDb[_hash1];
            Assert.AreEqual(null, getResult);
        }
        
        [Test]
        public void Set_commit_get()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            inMemoryDb[_hash1] = _bytes1;
            inMemoryDb.Commit();
            byte[] getResult = inMemoryDb[_hash1];
            Assert.AreEqual(_bytes1, getResult);
        }
        
        [Test]
        public void Set_commit_delete_restore_get()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            inMemoryDb[_hash1] = _bytes1;
            inMemoryDb.Commit();
            inMemoryDb.Delete(_hash1);
            inMemoryDb.Restore(-1);
            byte[] getResult = inMemoryDb[_hash1];
            Assert.AreEqual(_bytes1, getResult);
        }
        
        [Test]
        public void Set_delete_set_get()
        {
            InMemoryDb inMemoryDb = new InMemoryDb();
            inMemoryDb[_hash1] = _bytes1;
            inMemoryDb.Delete(_hash1);
            inMemoryDb[_hash1] = _bytes2;
            byte[] getResult = inMemoryDb[_hash1];
            Assert.AreEqual(_bytes2, getResult);
        }
    }
}