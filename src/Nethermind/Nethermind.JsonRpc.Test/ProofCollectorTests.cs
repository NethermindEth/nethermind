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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    public class ProofCollectorTests
    {
        [Test]
        public void Balance_is_correct()
        {
            ProofCollector proofCollector = new ProofCollector(TestItem.AddressA);
            StateTree tree = new StateTree();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);

            tree.Accept(proofCollector, new MemDb());

            AccountProof proof = proofCollector.AccountProof;

            Assert.AreEqual(UInt256.One, proof.Balance);
        }

        [Test]
        public void Code_hash_is_correct()
        {
            ProofCollector proofCollector = new ProofCollector(TestItem.AddressA);
            StateTree tree = new StateTree();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithCode(code).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);

            tree.Accept(proofCollector, new MemDb());

            AccountProof proof = proofCollector.AccountProof;

            Assert.AreEqual(Keccak.Compute(code), proof.CodeHash);
        }
        
        [Test]
        public void Nonce_is_correct()
        {
            ProofCollector proofCollector = new ProofCollector(TestItem.AddressA);
            StateTree tree = new StateTree();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithNonce(UInt256.One).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);

            tree.Accept(proofCollector, new MemDb());

            AccountProof proof = proofCollector.AccountProof;

            Assert.AreEqual(account1.Nonce, proof.Nonce);
        }
        
        [Test]
        public void Storage_root_is_correct()
        {
            ProofCollector proofCollector = new ProofCollector(TestItem.AddressA);
            StateTree tree = new StateTree();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);

            tree.Accept(proofCollector, new MemDb());

            AccountProof proof = proofCollector.AccountProof;

            Assert.AreEqual(TestItem.KeccakA, proof.StorageRoot);
        }
    }
}