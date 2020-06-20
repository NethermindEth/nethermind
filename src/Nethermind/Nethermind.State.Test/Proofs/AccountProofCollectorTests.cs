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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Store.Test.Proofs
{
    public class AccountProofCollectorTests
    {
        [Test]
        public void Non_existing_account_is_valid()
        {
            StateTree tree = new StateTree();
            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new UInt256[] {1, 2, 3});
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(TestItem.AddressA, proof.Address);
            Assert.AreEqual(Keccak.OfAnEmptyString, proof.CodeHash);
            Assert.AreEqual(Keccak.EmptyTreeHash, proof.StorageRoot);
            Assert.AreEqual(UInt256.Zero, proof.Balance);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[0].Value);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[1].Value);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[2].Value);
        }

        [Test]
        public void Non_existing_account_is_valid_on_non_empty_tree_with_branch_without_matching_child()
        {
            StateTree tree = new StateTree();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressC, new UInt256[] {1, 2, 3});
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            proof.Proof.Should().HaveCount(1);
            Assert.AreEqual(TestItem.AddressC, proof.Address);
            Assert.AreEqual(Keccak.OfAnEmptyString, proof.CodeHash);
            Assert.AreEqual(Keccak.EmptyTreeHash, proof.StorageRoot);
            Assert.AreEqual(UInt256.Zero, proof.Balance);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[0].Value);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[1].Value);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[2].Value);
        }

        [Test]
        public void Non_existing_account_is_valid_even_when_leaf_is_the_last_part_of_the_proof()
        {
            StateTree tree = new StateTree();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Commit();

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressC, new UInt256[] {1, 2, 3});
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            proof.Proof.Should().HaveCount(1);
            Assert.AreEqual(TestItem.AddressC, proof.Address);
            Assert.AreEqual(Keccak.OfAnEmptyString, proof.CodeHash);
            Assert.AreEqual(Keccak.EmptyTreeHash, proof.StorageRoot);
            Assert.AreEqual(UInt256.Zero, proof.Balance);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[0].Value);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[1].Value);
            Assert.AreEqual(new byte[] {0}, proof.StorageProofs[2].Value);
        }

        [Test]
        public void Non_existing_account_is_valid_even_when_extension_on_the_way_is_not_fully_matched()
        {
            // extension for a & b of the same length as for the c & d
            byte[] a = Bytes.FromHexString("0xeeeeeeeeeeeeeeeeeeeeeeee0eeeeeeeeeeeeeeeee1111111111111111111111");
            byte[] b = Bytes.FromHexString("0xeeeeeeeeeeeeeeeeeeeeeeee0eeeeeeeeeeeeeeeee2222222222222222222222");
            // but the extensions themselves have a difference in the middle (0 instead of e)
            byte[] c = Bytes.FromHexString("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee3333333333333333333333");
            byte[] d = Bytes.FromHexString("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee4444444444444444444444");

            StateTree tree = new StateTree();

            // we ensure that accounts a and b do not exist in the trie
            Account account = Build.An.Account.WithBalance(1).TestObject;
            tree.Set(c.AsSpan(), Rlp.Encode(account.WithChangedBalance(3)));
            tree.Set(d.AsSpan(), Rlp.Encode(account.WithChangedBalance(4)));
            tree.Commit();

            // now wer are looking for a trying to trick the code to think that the extension of c and d is a good match
            // if everything is ok the proof length of 1 is enough since the extension from the root is not matched
            AccountProofCollector accountProofCollector = new AccountProofCollector(a);
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            proof.Proof.Should().HaveCount(1);

            // and because the account does not exist, the balance should be 0
            proof.Balance.Should().Be(UInt256.Zero);
        }

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
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(TestItem.AddressA, proof.Address);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash, true);
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
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(UInt256.One, proof.Balance);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash, true);
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
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(account1.CodeHash, proof.CodeHash);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash, true);
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
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(account1.Nonce, proof.Nonce);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash, true);
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
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual(TestItem.KeccakA, proof.StorageRoot);

            AccountProofCollector accountProofCollector2 = new AccountProofCollector(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash, true);
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
            tree.Accept(accountProofCollector, tree.RootHash, true);
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
            tree.Accept(accountProofCollector, tree.RootHash, true);
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
            tree.Accept(accountProofCollector, tree.RootHash, true);
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
            tree.Accept(accountProofCollector, tree.RootHash, true);
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
            tree.Accept(dumper, tree.RootHash, true);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, b, c});
            tree.Accept(accountProofCollector, tree.RootHash, true);
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
            tree.Accept(dumper, tree.RootHash, true);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, b, c, d, e});
            tree.Accept(accountProofCollector, tree.RootHash, true);
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
            tree.Accept(dumper, tree.RootHash, true);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, b, c, d, e});
            tree.Accept(accountProofCollector, tree.RootHash, true);
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
            tree.Accept(dumper, tree.RootHash, true);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, b, c, d, e});
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Value?.ToHexString(true) ?? "0x");
            Assert.AreEqual("0x00", proof.StorageProofs[1].Value?.ToHexString(true) ?? "0x");
            Assert.AreEqual("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[2].Value?.ToHexString(true) ?? "0x");
            Assert.AreEqual("0x00", proof.StorageProofs[3].Value?.ToHexString(true) ?? "0x");
            Assert.AreEqual("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[4].Value?.ToHexString(true) ?? "0x");

            proof.StorageProofs[1].Proof.Should().HaveCount(3);
            proof.StorageProofs[3].Proof.Should().HaveCount(2);
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
            tree.Accept(dumper, tree.RootHash, true);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual((UInt256) 0, proof.Balance);
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
            tree.Accept(dumper, tree.RootHash, true);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new AccountProofCollector(TestItem.AddressA, new byte[][] {a, c, e});
            tree.Accept(accountProofCollector, tree.RootHash, true);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.AreEqual("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[0].Value.ToHexString(true));
            Assert.AreEqual("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[1].Value.ToHexString(true));
            Assert.AreEqual("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000", proof.StorageProofs[2].Value.ToHexString(true));
        }

        private class AddressWithStorage
        {
            public Address Address { get; set; }
            public StorageCell[] StorageCells { get; set; }
        }

        public static IEnumerable<string> HistoricallyFailing()
        {
            yield return @"0xe0f5c856d19fcddb0cfa2c4064ec919adafb6d80
10
storage: 90190839119413396184166530989136716919557171021730141079211746929376167847506
storage: 60280810468973099635748215219305738970151609591133611700378163748162582217500
storage: 56784196270920297582192371114849834395585818651844848895453647126336316403277
storage: 11386053319507299849984854470075872439051848133270174841462911687671544071707
storage: 16949050371107301881612419259959869481202105495086891031877022063005810823309
storage: 105439431600450810563412045613438563546051217119557933010987835252030481233747
storage: 37040886443204616993637977132610307172275531978457164685255960146037055017197
storage: 104735236760362913007298474508387658822479012968314539318185042389577739100246
storage: 108669636295405922971679648935718736204860814839538212270739579563052669465591
storage: 108107370366072374244910370819581019716912846239498832858157557821586637571375";

            yield return @"0xca1ba09d7cb9b1721ccac53fa4249bb02a42e73b
8
storage: 26591015056255940773561727274931264728284725603300833689614243940390408939488
storage: 59636747350720526261506159639489373892458167273661909165130528130031474052838
storage: 106999041675628775514034988140391089841832256464325898872971433020677893718690
storage: 26866574295138333182318663188565922833787941567587071137020724631879121967788
storage: 28092881877241495226485124166299198735773611223808245687554119868296805169797
storage: 84933130979025498209423318463450575026490316299746625277292006181668780209478
storage: 26715270868346283377335335026251601733397604213965076966634040326119506469337
storage: 51382510465573486824919678063890693013564109356622678137814928589010190821165";

            yield return @"0xd642bbcf4b1551c49c731f67f3213aa3efc87c66
10
storage: 113228680663035796763681024334681345559304969542058231169384226258298958122364
storage: 35860861552134812047842996036279056736220017564947241929791917707054732093120
storage: 52820586150044207685821363947948392442954759797151605299107327382863648053676
storage: 57769764136880055766842070521928791956350359633169796902606177082538406303438
storage: 53694035777852814230393852245000427518891756588469681045801661758298647004960
storage: 3798594443022351724432742855735217711917573391371340766076978680019981191414
storage: 45914278961934660192043383587566930347966728874794866557797715560323628836825
storage: 86468423934361980950853246580470861336081992405846211806025626285841069896350
storage: 112436656173778403373985610282509959985335412200705167310797609894568146434849
storage: 92162443411492722621002940415637079289372194606037573399727937793691328356713";

            yield return @"0xb7c8764b01b9bdb562eb94b9c770a8142fce39fb
6
storage: 73717329750280397719442433697402854575658880881377731886260787604497413625654
storage: 50195900406201868652709285130858962234301383997276891792317317427378623465986
storage: 106520478213404907526508520015705073661086771623270596068772806520925153167051
storage: 70113663947881986261655331660189759575019977222439240381827660617673604039899
storage: 56367319856030301693302216007185758915298146888787088587870229767075273274000
storage: 100752081440875945650171672492180468922677364319148698288550774159260310722987";
        }

        [TestCaseSource(nameof(HistoricallyFailing))]
        public void _Test_storage_failed_case(string historicallyFailingCase)
        {
            string[] lines = historicallyFailingCase.Split(Environment.NewLine);
            int storageCount = lines.Length - 2;

            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);

            Address address = new Address(Bytes.FromHexString(lines[0]));
            int accountIndex = int.Parse(lines[1]);
            UInt256[] indexes = new UInt256[storageCount];
            for (int j = 0; j < storageCount; j++)
            {
                indexes[j] = UInt256.Parse(lines[j + 2].Replace("storage: ", string.Empty));
            }

            AddressWithStorage addressWithStorage = new AddressWithStorage();
            addressWithStorage.StorageCells = new StorageCell[storageCount];
            addressWithStorage.Address = address;

            StorageTree storageTree = new StorageTree(memDb);
            for (int j = 0; j < storageCount; j++)
            {
                UInt256 index = UInt256.Parse(lines[j + 2].Replace("storage: ", string.Empty));
                StorageCell storageCell = new StorageCell(address, index);
                addressWithStorage.StorageCells[j] = storageCell;
                byte[] rawKey = new byte[32];
                addressWithStorage.StorageCells[j].Index.ToBigEndian(rawKey);
                TestContext.WriteLine($"Set {Keccak.Compute(rawKey).Bytes.ToHexString()}");
                storageTree.Set(addressWithStorage.StorageCells[j].Index, new byte[] {1});
                storageTree.UpdateRootHash();
                storageTree.Commit();
            }

            Account account = Build.An.Account.WithBalance((UInt256) accountIndex).WithStorageRoot(storageTree.RootHash).TestObject;
            tree.Set(addressWithStorage.Address, account);

            tree.UpdateRootHash();
            tree.Commit();
            
            TreeDumper treeDumper = new TreeDumper();
            tree.Accept(treeDumper, tree.RootHash, true);
            TestContext.WriteLine(treeDumper.ToString());

            AccountProofCollector collector = new AccountProofCollector(address, indexes);
            tree.Accept(collector, tree.RootHash, true);

            AccountProof accountProof = collector.BuildResult();
            accountProof.Address.Should().Be(address);
            accountProof.Balance.Should().Be((UInt256) accountIndex);
            accountProof.Nonce.Should().Be(0);
            accountProof.CodeHash.Should().Be(Keccak.OfAnEmptyString);
            if (accountIndex != 0) accountProof.StorageRoot.Should().NotBe(Keccak.EmptyTreeHash);
            accountProof.StorageProofs.Length.Should().Be(accountIndex);

            for (int j = 0; j < accountProof.StorageProofs.Length; j++)
            {
                TrieNode node = new TrieNode(NodeType.Unknown, accountProof.StorageProofs[j].Proof.Last());
                node.ResolveNode(null);
                if (node.Value.Length != 1)
                {
                    TestContext.WriteLine($"{j}");
                    // throw new InvalidDataException($"{j}");
                }
            }
        }

        [Test]
        public void Chaotic_test()
        {
            const int accountsCount = 100;

            CryptoRandom random = new CryptoRandom();
            List<AddressWithStorage> addressesWithStorage = new List<AddressWithStorage>();

            for (int i = 0; i < accountsCount; i++)
            {
                AddressWithStorage addressWithStorage = new AddressWithStorage();
                addressWithStorage.StorageCells = new StorageCell[i];
                byte[] addressBytes = random.GenerateRandomBytes(20);
                addressWithStorage.Address = new Address(addressBytes);

                for (int j = 0; j < i; j++)
                {
                    byte[] storageIndex = random.GenerateRandomBytes(32);
                    UInt256.CreateFromBigEndian(out UInt256 index, storageIndex);
                    StorageCell storageCell = new StorageCell(addressWithStorage.Address, index);
                    addressWithStorage.StorageCells[j] = storageCell;
                }

                addressesWithStorage.Add(addressWithStorage);
            }

            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);

            for (int i = 0; i < accountsCount; i++)
            {
                Account account = Build.An.Account.WithBalance((UInt256) i).TestObject;
                StorageTree storageTree = new StorageTree(memDb);
                for (int j = 0; j < i; j++)
                {
                    storageTree.Set(addressesWithStorage[i].StorageCells[j].Index, new byte[1] {1});
                }

                storageTree.UpdateRootHash();
                storageTree.Commit();

                account = account.WithChangedStorageRoot(storageTree.RootHash);
                tree.Set(addressesWithStorage[i].Address, account);
            }

            tree.UpdateRootHash();
            tree.Commit();

            for (int i = 0; i < accountsCount; i++)
            {
                AccountProofCollector collector = new AccountProofCollector(addressesWithStorage[i].Address, addressesWithStorage[i].StorageCells.Select(sc => sc.Index).ToArray());
                tree.Accept(collector, tree.RootHash, true);

                AccountProof accountProof = collector.BuildResult();
                accountProof.Address.Should().Be(addressesWithStorage[i].Address);
                accountProof.Balance.Should().Be((UInt256) i);
                accountProof.Nonce.Should().Be(0);
                accountProof.CodeHash.Should().Be(Keccak.OfAnEmptyString);
                if (i != 0) accountProof.StorageRoot.Should().NotBe(Keccak.EmptyTreeHash);
                accountProof.StorageProofs.Length.Should().Be(i);

                for (int j = 0; j < i; j++)
                {
                    byte[] indexBytes = new byte[32];
                    addressesWithStorage[i].StorageCells[j].Index.ToBigEndian(indexBytes.AsSpan());
                    accountProof.StorageProofs[j].Key.ToHexString().Should().Be(indexBytes.ToHexString(), $"{i} {j}");

                    TrieNode node = new TrieNode(NodeType.Unknown, accountProof.StorageProofs[j].Proof.Last());
                    node.ResolveNode(null);
                    // TestContext.Write($"|[{i},{j}]");
                    if (node.Value.Length != 1)
                    {
                        TestContext.WriteLine();
                        TestContext.WriteLine(addressesWithStorage[i].Address);
                        TestContext.WriteLine(i);
                        foreach (StorageCell storageCell in addressesWithStorage[i].StorageCells)
                        {
                            TestContext.WriteLine("storage: " + storageCell.Index);
                        }
                    }

                    node.Value.Should().BeEquivalentTo(new byte[] {1});
                }
            }
        }
    }
}