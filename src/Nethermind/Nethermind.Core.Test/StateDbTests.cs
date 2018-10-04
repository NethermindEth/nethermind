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

using Nethermind.Core.Crypto;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class StateDbTests
    {
        private Keccak _hash1 = Keccak.Compute("1");
        private Keccak _hash2 = Keccak.Compute(Keccak.Compute("1").Bytes);

        private readonly byte[] _bytes1 = new byte[] {1};
        private readonly byte[] _bytes2 = new byte[] {2};

        [Test]
        public void Set_get()
        {
            StateDb db = new StateDb(new MemDb());
            db.Set(_hash1, _bytes1);
            byte[] getResult = db.Get(_hash1);
            Assert.AreEqual(_bytes1, getResult);
        }
        
        [Test]
        public void Double_set_get()
        {
            StateDb db = new StateDb(new MemDb());
            db.Set(_hash1, _bytes1);
            db.Set(_hash1, _bytes1);
            byte[] getResult = db.Get(_hash1);
            Assert.AreEqual(_bytes1, getResult);
        }
        
        [Test]
        public void Initial_take_snapshot()
        {
            StateDb db = new StateDb(new MemDb());
            Assert.AreEqual(-1, db.TakeSnapshot());
        }
        
        [Test]
        public void Set_take_snapshot()
        {
            StateDb db = new StateDb(new MemDb());
            db.Set(_hash1, _bytes1);
            Assert.AreEqual(0, db.TakeSnapshot());
        }
        
        [Test]
        public void Set_restore_get()
        {
            StateDb db = new StateDb(new MemDb());
            db.Set(_hash1, _bytes1);
            db.Restore(-1);
            byte[] getResult = db.Get(_hash1);
            Assert.AreEqual(null, getResult);
        }
        
        [Test]
        public void Set_commit_get()
        {
            StateDb db = new StateDb(new MemDb());
            db.Set(_hash1, _bytes1);
            db.Commit();
            byte[] getResult = db.Get(_hash1);
            Assert.AreEqual(_bytes1, getResult);
        }
        
        [Test]
        public void Restore_in_the_middle()
        {
            StateDb db = new StateDb(new MemDb());
            db.Set(_hash1, _bytes1);
            int snapshot = db.TakeSnapshot();
            db.Set(_hash2, _bytes2);
            db.Restore(snapshot);
            byte[] getResult = db.Get(_hash2);
            Assert.IsNull(getResult);
        }
        
        [Test]
        public void Capacity_grwoth_and_shrinkage()
        {
            StateDb db = new StateDb(new MemDb());
            for (int i = 0; i < 16; i++)
            {
                _hash1 = Keccak.Compute(_hash1.Bytes); 
                db.Set(_hash1, _bytes1);
            }
            
            db.Restore(-1);
            
            byte[] getResult = db.Get(_hash1);
            Assert.AreEqual(null, getResult);
            
            for (int i = 0; i < 16; i++)
            {
                _hash1 = Keccak.Compute(_hash1.Bytes);
                db.Set(_hash1, _bytes1);
            }
            
            db.Commit();
            
            getResult = db.Get(_hash1);
            Assert.AreEqual(_bytes1, getResult);
        }
    }
}