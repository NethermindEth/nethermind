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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Serialization.Rlp;
using Nethermind.Store.Proofs;
using NUnit.Framework;

namespace Nethermind.Store.Test.Proofs
{
    public class AccountProofCollectorTests
    {
        [Test]
        public void Addresses_are_correct()
        {
            StateTree tree = new StateTree();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(TestItem.AddressA, proof.Address);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.AreEqual(TestItem.AddressB, proof2.Address);
        }
        
        [Test]
        public void Balance_is_correct()
        {
            StateTree tree = new StateTree();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(UInt256.One, proof.Balance);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.AreEqual(UInt256.One + 1, proof2.Balance);
        }

        [Test]
        public void Code_hash_is_correct()
        {
            StateTree tree = new StateTree();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithCode(code).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(account1.CodeHash, proof.CodeHash);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.AreEqual(Keccak.OfAnEmptyString, proof2.CodeHash);
        }

        [Test]
        public void Nonce_is_correct()
        {
            StateTree tree = new StateTree();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithNonce(UInt256.One).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(account1.Nonce, proof.Nonce);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.AreEqual(UInt256.Zero, proof2.Nonce);
        }

        [Test]
        public void Storage_root_is_correct()
        {
            StateTree tree = new StateTree();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(TestItem.KeccakA, proof.StorageRoot);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.AreEqual(Keccak.EmptyTreeHash, proof2.StorageRoot);
        }

        [Test]
        public void Proof_path_is_filled()
        {
            StateTree tree = new StateTree();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(3, proof.Proof.Length);
        }

        [Test]
        public void Storage_proofs_length_is_as_expected()
        {
            StateTree tree = new StateTree();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new[] {Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001")});
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(2, proof.StorageProofs.Length);
        }

        [Test]
        public void Storage_proofs_have_values_set()
        {
            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);
            StorageTree storageTree = new StorageTree(memDb);
            storageTree.Set(UInt256.Zero, Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            storageTree.Set(UInt256.One, Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            storageTree.Commit();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new[] {Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001")});
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Value.ToHexString(true));
            Assert.AreEqual("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[1].Value.ToHexString(true));
        }

        [Test]
        public void Storage_proofs_have_keys_set()
        {
            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);
            StorageTree storageTree = new StorageTree(memDb);
            storageTree.Set(UInt256.Zero, Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            storageTree.Set(UInt256.One, Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            storageTree.Commit();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new[] {Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001")});
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0x0000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Key.ToHexString(true));
            Assert.AreEqual("0x0000000000000000000000000000000000000000000000000000000000000001", proof.StorageProofs[1].Key.ToHexString(true));
        }

        [Test]
        public void Storage_proofs_have_values_set_complex_setup()
        {
            byte[] a = Bytes.FromHexString("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            byte[] b = Bytes.FromHexString("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            byte[] c = Bytes.FromHexString("0x0000000000cccccccccccccccccccccccccccccccccccccccccccccccccccccc");

            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);
            StorageTree storageTree = new StorageTree(memDb);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            TreeDumper dumper = new TreeDumper();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, b, c});
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Value.ToHexString(true));
            Assert.AreEqual("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[1].Value.ToHexString(true));
            Assert.AreEqual("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[2].Value.ToHexString(true));
        }

        [Test]
        public void Storage_proofs_have_values_set_complex_2_setup()
        {
            byte[] a = Bytes.FromHexString("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            byte[] b = Bytes.FromHexString("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            byte[] c = Bytes.FromHexString("0x0000000000cccccccccccccccccccccccccccccccccccccccccccccccccccccc");
            byte[] d = Bytes.FromHexString("0x0000000000dddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            byte[] e = Bytes.FromHexString("0x0000000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);
            StorageTree storageTree = new StorageTree(memDb);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(d).Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            TreeDumper dumper = new TreeDumper();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, b, c, d, e});
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Value.ToHexString(true));
            Assert.AreEqual("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[1].Value.ToHexString(true));
            Assert.AreEqual("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[2].Value.ToHexString(true));
            Assert.AreEqual("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[3].Value.ToHexString(true));
            Assert.AreEqual("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[4].Value.ToHexString(true));
        }

        [Test]
        public void Storage_proofs_have_values_set_complex_3_setup()
        {
            byte[] a = Bytes.FromHexString("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            byte[] b = Bytes.FromHexString("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            byte[] c = Bytes.FromHexString("0x00000000001ccccccccccccccccccccccccccccccccccccccccccccccccccccc");
            byte[] d = Bytes.FromHexString("0x00000000001ddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            byte[] e = Bytes.FromHexString("0x00000000001eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);
            StorageTree storageTree = new StorageTree(memDb);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(d).Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            TreeDumper dumper = new TreeDumper();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, b, c, d, e});
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Value.ToHexString(true));
            Assert.AreEqual("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[1].Value.ToHexString(true));
            Assert.AreEqual("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[2].Value.ToHexString(true));
            Assert.AreEqual("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[3].Value.ToHexString(true));
            Assert.AreEqual("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[4].Value.ToHexString(true));
        }

        [Test]
        public void Storage_proofs_when_values_are_missing_setup()
        {
            byte[] a = Bytes.FromHexString("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            byte[] b = Bytes.FromHexString("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            byte[] c = Bytes.FromHexString("0x00000000001ccccccccccccccccccccccccccccccccccccccccccccccccccccc");
            byte[] d = Bytes.FromHexString("0x00000000001ddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            byte[] e = Bytes.FromHexString("0x00000000001eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);
            StorageTree storageTree = new StorageTree(memDb);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            TreeDumper dumper = new TreeDumper();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, b, c, d, e});
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Value?.ToHexString(true) ?? "0x");
            Assert.AreEqual("0x", proof.StorageProofs[1].Value?.ToHexString(true) ?? "0x");
            Assert.AreEqual("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[2].Value?.ToHexString(true) ?? "0x");
            Assert.AreEqual("0x", proof.StorageProofs[3].Value?.ToHexString(true) ?? "0x");
            Assert.AreEqual("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[4].Value?.ToHexString(true) ?? "0x");
        }

        [Test]
        public void Shows_empty_values_when_account_is_missing()
        {
            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);

            byte[] code = new byte[] {1, 2, 3};
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            TreeDumper dumper = new TreeDumper();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual((UInt256) 2, proof.Balance);
            Assert.AreEqual(UInt256.Zero, proof.Nonce);
            Assert.AreEqual(Keccak.OfAnEmptyString, proof.CodeHash);
            Assert.AreEqual(Keccak.EmptyTreeHash, proof.StorageRoot);
        }

        [Test]
        public void Storage_proofs_have_values_set_selective_setup()
        {
            byte[] a = Bytes.FromHexString("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            byte[] b = Bytes.FromHexString("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            byte[] c = Bytes.FromHexString("0x00000000001ccccccccccccccccccccccccccccccccccccccccccccccccccccc");
            byte[] d = Bytes.FromHexString("0x00000000001ddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            byte[] e = Bytes.FromHexString("0x00000000001eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);
            StorageTree storageTree = new StorageTree(memDb);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(d).Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            TreeDumper dumper = new TreeDumper();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, c, e});
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Value.ToHexString(true));
            Assert.AreEqual("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[1].Value.ToHexString(true));
            Assert.AreEqual("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[2].Value.ToHexString(true));
        }
    }
}