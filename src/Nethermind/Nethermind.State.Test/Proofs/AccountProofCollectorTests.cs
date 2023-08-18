// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test.Proofs
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class AccountProofCollectorTests
    {
        [Test]
        public void Non_existing_account_is_valid()
        {
            StateTree tree = new();
            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new UInt256[] { 1, 2, 3 });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.Address, Is.EqualTo(TestItem.AddressA));
            Assert.That(proof.CodeHash, Is.EqualTo(Keccak.OfAnEmptyString));
            Assert.That(proof.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(proof.CodeHash, Is.EqualTo(ValueKeccak.OfAnEmptyString));
            Assert.That(proof.StorageRoot, Is.EqualTo(ValueKeccak.EmptyTreeHash));
            Assert.That(proof.Balance, Is.EqualTo(UInt256.Zero));
            Assert.That(proof.StorageProofs[0].Value, Is.EqualTo(new byte[] { 0 }));
            Assert.That(proof.StorageProofs[1].Value, Is.EqualTo(new byte[] { 0 }));
            Assert.That(proof.StorageProofs[2].Value, Is.EqualTo(new byte[] { 0 }));
        }

        [Test]
        public void Non_existing_account_is_valid_on_non_empty_tree_with_branch_without_matching_child()
        {
            StateTree tree = new();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressC, new UInt256[] { 1, 2, 3 });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            proof.Proof.Should().HaveCount(1);
            Assert.That(proof.Address, Is.EqualTo(TestItem.AddressC));
            Assert.That(proof.CodeHash, Is.EqualTo(Keccak.OfAnEmptyString));
            Assert.That(proof.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(proof.Balance, Is.EqualTo(UInt256.Zero));
            Assert.That(proof.StorageProofs[0].Value, Is.EqualTo(new byte[] { 0 }));
            Assert.That(proof.StorageProofs[1].Value, Is.EqualTo(new byte[] { 0 }));
            Assert.That(proof.StorageProofs[2].Value, Is.EqualTo(new byte[] { 0 }));
        }

        [Test]
        public void Non_existing_account_is_valid_even_when_leaf_is_the_last_part_of_the_proof()
        {
            StateTree tree = new();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressC, new UInt256[] { 1, 2, 3 });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            proof.Proof.Should().HaveCount(1);
            Assert.That(proof.Address, Is.EqualTo(TestItem.AddressC));
            Assert.That(proof.CodeHash, Is.EqualTo(Keccak.OfAnEmptyString));
            Assert.That(proof.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(proof.Balance, Is.EqualTo(UInt256.Zero));
            Assert.That(proof.StorageProofs[0].Value, Is.EqualTo(new byte[] { 0 }));
            Assert.That(proof.StorageProofs[1].Value, Is.EqualTo(new byte[] { 0 }));
            Assert.That(proof.StorageProofs[2].Value, Is.EqualTo(new byte[] { 0 }));
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

            StateTree tree = new();

            // we ensure that accounts a and b do not exist in the trie
            Account account = Build.An.Account.WithBalance(1).TestObject;
            tree.Set(c.AsSpan(), Rlp.Encode(account.WithChangedBalance(3)));
            tree.Set(d.AsSpan(), Rlp.Encode(account.WithChangedBalance(4)));
            tree.Commit(0);

            // now wer are looking for a trying to trick the code to think that the extension of c and d is a good match
            // if everything is ok the proof length of 1 is enough since the extension from the root is not matched
            AccountProofCollector accountProofCollector = new(a);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            proof.Proof.Should().HaveCount(1);

            // and because the account does not exist, the balance should be 0
            proof.Balance.Should().Be(UInt256.Zero);
        }

        [Test]
        public void Addresses_are_correct()
        {
            StateTree tree = new();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.Address, Is.EqualTo(TestItem.AddressA));

            AccountProofCollector accountProofCollector2 = new(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.That(proof2.Address, Is.EqualTo(TestItem.AddressB));
        }

        [Test]
        public void Balance_is_correct()
        {
            StateTree tree = new();

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.Balance, Is.EqualTo(UInt256.One));

            AccountProofCollector accountProofCollector2 = new(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.That(proof2.Balance, Is.EqualTo(UInt256.One + 1));
        }

        [Test]
        public void Code_hash_is_correct()
        {
            StateTree tree = new();

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithCode(code).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.CodeHash, Is.EqualTo(account1.CodeHash));

            AccountProofCollector accountProofCollector2 = new(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.That(proof2.CodeHash, Is.EqualTo(Keccak.OfAnEmptyString));
        }

        [Test]
        public void Nonce_is_correct()
        {
            StateTree tree = new();

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithNonce(UInt256.One).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.Nonce, Is.EqualTo(account1.Nonce));

            AccountProofCollector accountProofCollector2 = new(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.That(proof2.Nonce, Is.EqualTo(UInt256.Zero));
        }

        [Test]
        public void Storage_root_is_correct()
        {
            StateTree tree = new();

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageRoot, Is.EqualTo(TestItem.KeccakA));

            AccountProofCollector accountProofCollector2 = new(TestItem.AddressB);
            tree.Accept(accountProofCollector2, tree.RootHash);
            AccountProof proof2 = accountProofCollector2.BuildResult();
            Assert.That(proof2.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
        }

        [Test]
        public void Proof_path_is_filled()
        {
            StateTree tree = new();

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.Proof.Length, Is.EqualTo(3));
        }

        [Test]
        public void Storage_proofs_length_is_as_expected()
        {
            StateTree tree = new();

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new[] { Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001") });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageProofs.Length, Is.EqualTo(2));
        }

        [Test]
        public void Storage_proofs_have_values_set()
        {
            IDb memDb = new MemDb();
            TrieStore trieStore = new(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(UInt256.Zero, Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            storageTree.Set(UInt256.One, Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            storageTree.Commit(0);

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new[] { Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001") });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageProofs[0].Value.ToHexString(true), Is.EqualTo("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[1].Value.ToHexString(true), Is.EqualTo("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Storage_proofs_have_keys_set()
        {
            IDb memDb = new MemDb();
            ITrieStore trieStore = new TrieStore(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(UInt256.Zero, Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            storageTree.Set(UInt256.One, Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            storageTree.Commit(0);

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new[] { Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001") });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageProofs[0].Key.ToHexString(true), Is.EqualTo("0x0000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[1].Key.ToHexString(true), Is.EqualTo("0x0000000000000000000000000000000000000000000000000000000000000001"));
        }

        [Test]
        public void Storage_proofs_have_values_set_complex_setup()
        {
            byte[] a = Bytes.FromHexString("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            byte[] b = Bytes.FromHexString("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            byte[] c = Bytes.FromHexString("0x0000000000cccccccccccccccccccccccccccccccccccccccccccccccccccccc");

            IDb memDb = new MemDb();
            TrieStore trieStore = new(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit(0);

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            TreeDumper dumper = new();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new byte[][] { a, b, c });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageProofs[0].Value.ToHexString(true), Is.EqualTo("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[1].Value.ToHexString(true), Is.EqualTo("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[2].Value.ToHexString(true), Is.EqualTo("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000"));
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
            TrieStore trieStore = new(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(d).Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit(0);

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            TreeDumper dumper = new();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new byte[][] { a, b, c, d, e });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageProofs[0].Value.ToHexString(true), Is.EqualTo("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[1].Value.ToHexString(true), Is.EqualTo("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[2].Value.ToHexString(true), Is.EqualTo("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[3].Value.ToHexString(true), Is.EqualTo("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[4].Value.ToHexString(true), Is.EqualTo("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000"));
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
            TrieStore trieStore = new(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(d).Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit(0);

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            TreeDumper dumper = new();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new byte[][] { a, b, c, d, e });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageProofs[0].Value.ToHexString(true), Is.EqualTo("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[1].Value.ToHexString(true), Is.EqualTo("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[2].Value.ToHexString(true), Is.EqualTo("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[3].Value.ToHexString(true), Is.EqualTo("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[4].Value.ToHexString(true), Is.EqualTo("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000"));
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
            TrieStore trieStore = new(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit(0);

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            TreeDumper dumper = new();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new byte[][] { a, b, c, d, e });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageProofs[0].Value?.ToHexString(true) ?? "0x", Is.EqualTo("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[1].Value?.ToHexString(true) ?? "0x", Is.EqualTo("0x00"));
            Assert.That(proof.StorageProofs[2].Value?.ToHexString(true) ?? "0x", Is.EqualTo("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[3].Value?.ToHexString(true) ?? "0x", Is.EqualTo("0x00"));
            Assert.That(proof.StorageProofs[4].Value?.ToHexString(true) ?? "0x", Is.EqualTo("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000"));

            proof.StorageProofs[1].Proof.Should().HaveCount(3);
            proof.StorageProofs[3].Proof.Should().HaveCount(2);
        }

        [Test]
        public void Shows_empty_values_when_account_is_missing()
        {
            IDb memDb = new MemDb();
            StateTree tree = new(new TrieStore(memDb, LimboLogs.Instance), LimboLogs.Instance);

            byte[] code = new byte[] { 1, 2, 3 };
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            TreeDumper dumper = new();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new(TestItem.AddressA);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.Balance, Is.EqualTo((UInt256)0));
            Assert.That(proof.Nonce, Is.EqualTo(UInt256.Zero));
            Assert.That(proof.CodeHash, Is.EqualTo(Keccak.OfAnEmptyString));
            Assert.That(proof.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
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
            TrieStore trieStore = new(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(d).Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit(0);

            byte[] code = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit(0);

            TreeDumper dumper = new();
            tree.Accept(dumper, tree.RootHash);
            Console.WriteLine(dumper.ToString());

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new byte[][] { a, c, e });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();
            Assert.That(proof.StorageProofs[0].Value.ToHexString(true), Is.EqualTo("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[1].Value.ToHexString(true), Is.EqualTo("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            Assert.That(proof.StorageProofs[2].Value.ToHexString(true), Is.EqualTo("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000"));
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
            string splitter = historicallyFailingCase.Contains("\r\n") ? "\r\n" : "\n"; //Running Windows 11 "Environment.NewLine" was returning \r\n when in string \n was used - this may be more stable.
            string[] lines = historicallyFailingCase.Split(splitter);
            int storageCount = lines.Length - 2;

            IDb memDb = new MemDb();
            TrieStore trieStore = new(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);

            Address address = new(Bytes.FromHexString(lines[0]));
            int accountIndex = int.Parse(lines[1]);
            UInt256[] indexes = new UInt256[storageCount];
            for (int j = 0; j < storageCount; j++)
            {
                indexes[j] = UInt256.Parse(lines[j + 2].Replace("storage: ", string.Empty));
            }

            AddressWithStorage addressWithStorage = new();
            addressWithStorage.StorageCells = new StorageCell[storageCount];
            addressWithStorage.Address = address;

            StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            for (int j = 0; j < storageCount; j++)
            {
                UInt256 index = UInt256.Parse(lines[j + 2].Replace("storage: ", string.Empty));
                StorageCell storageCell = new(address, index);
                addressWithStorage.StorageCells[j] = storageCell;
                byte[] rawKey = new byte[32];
                addressWithStorage.StorageCells[j].Index.ToBigEndian(rawKey);
                TestContext.WriteLine($"Set {Keccak.Compute(rawKey).Bytes.ToHexString()}");
                storageTree.Set(addressWithStorage.StorageCells[j].Index, new byte[] { 1 });
                storageTree.UpdateRootHash();
                storageTree.Commit(0);
            }

            Account account = Build.An.Account.WithBalance((UInt256)accountIndex).WithStorageRoot(storageTree.RootHash).TestObject;
            tree.Set(addressWithStorage.Address, account);

            tree.UpdateRootHash();
            tree.Commit(0);

            TreeDumper treeDumper = new();
            tree.Accept(treeDumper, tree.RootHash);
            TestContext.WriteLine(treeDumper.ToString());

            AccountProofCollector collector = new(address, indexes);
            tree.Accept(collector, tree.RootHash);

            AccountProof accountProof = collector.BuildResult();
            accountProof.Address.Should().Be(address);
            accountProof.Balance.Should().Be((UInt256)accountIndex);
            accountProof.Nonce.Should().Be(0);
            accountProof.CodeHash.Should().Be(Keccak.OfAnEmptyString);
            if (accountIndex != 0) accountProof.StorageRoot.Should().NotBe(Keccak.EmptyTreeHash);
            accountProof.StorageProofs.Length.Should().Be(accountIndex);

            for (int j = 0; j < accountProof.StorageProofs.Length; j++)
            {
                TrieNode node = new(NodeType.Unknown, accountProof.StorageProofs[j].Proof.Last());
                node.ResolveNode(new TrieStore(memDb, NullLogManager.Instance));
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

            CryptoRandom random = new();
            List<AddressWithStorage> addressesWithStorage = new();

            for (int i = 0; i < accountsCount; i++)
            {
                AddressWithStorage addressWithStorage = new();
                addressWithStorage.StorageCells = new StorageCell[i];
                byte[] addressBytes = random.GenerateRandomBytes(20);
                addressWithStorage.Address = new Address(addressBytes);

                for (int j = 0; j < i; j++)
                {
                    byte[] storageIndex = random.GenerateRandomBytes(32);
                    UInt256 index = new(storageIndex);
                    StorageCell storageCell = new(addressWithStorage.Address, index);
                    addressWithStorage.StorageCells[j] = storageCell;
                }

                addressesWithStorage.Add(addressWithStorage);
            }

            IDb memDb = new MemDb();
            TrieStore trieStore = new(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);

            for (int i = 0; i < accountsCount; i++)
            {
                Account account = Build.An.Account.WithBalance((UInt256)i).TestObject;
                StorageTree storageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
                for (int j = 0; j < i; j++)
                {
                    storageTree.Set(addressesWithStorage[i].StorageCells[j].Index, new byte[1] { 1 });
                }

                storageTree.UpdateRootHash();
                storageTree.Commit(0);

                account = account.WithChangedStorageRoot(storageTree.RootHash);
                tree.Set(addressesWithStorage[i].Address, account);
            }

            tree.UpdateRootHash();
            tree.Commit(0);

            for (int i = 0; i < accountsCount; i++)
            {
                AccountProofCollector collector = new(addressesWithStorage[i].Address, addressesWithStorage[i].StorageCells.Select(sc => sc.Index).ToArray());
                tree.Accept(collector, tree.RootHash);

                AccountProof accountProof = collector.BuildResult();
                accountProof.Address.Should().Be(addressesWithStorage[i].Address);
                accountProof.Balance.Should().Be((UInt256)i);
                accountProof.Nonce.Should().Be(0);
                accountProof.CodeHash.Should().Be(Keccak.OfAnEmptyString);
                if (i != 0) accountProof.StorageRoot.Should().NotBe(Keccak.EmptyTreeHash);
                accountProof.StorageProofs.Length.Should().Be(i);

                for (int j = 0; j < i; j++)
                {
                    byte[] indexBytes = new byte[32];
                    addressesWithStorage[i].StorageCells[j].Index.ToBigEndian(indexBytes.AsSpan());
                    accountProof.StorageProofs[j].Key.ToHexString().Should().Be(indexBytes.ToHexString(), $"{i} {j}");

                    TrieNode node = new(NodeType.Unknown, accountProof.StorageProofs[j].Proof.Last());
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

                    node.Value.Should().BeEquivalentTo(new byte[] { 1 });
                }
            }
        }
    }
}
