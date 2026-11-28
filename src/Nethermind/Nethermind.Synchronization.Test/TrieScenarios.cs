// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Test
{
    public static class TrieScenarios
    {
        private static bool Inited;

        public static Account Empty = null!;
        public static Account AccountJustState0 = null!;
        public static Account AccountJustState1 = null!;
        public static Account AccountJustState2 = null!;
        public static Account Account0 = null!;
        public static Account Account1 = null!;
        public static Account Account2 = null!;
        public static Account Account3 = null!;

        public static readonly byte[] Code0 = { 0, 0 };
        public static readonly byte[] Code1 = { 0, 1 };
        public static readonly byte[] Code2 = { 0, 2 };
        public static readonly byte[] Code3 = { 0, 3 };

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void InitOnce()
        {
            if (!Inited)
            {
                // this setup is just for finding the storage root
                StorageTree remoteStorageTree = SetStorage(new TestRawTrieStore(new MemDb()), TestItem.AddressA);
                Hash256 storageRoot = remoteStorageTree.RootHash;

                Empty = Build.An.Account.WithBalance(0).TestObject;
                Account0 = Build.An.Account.WithBalance(1).WithCode(Code0).WithStorageRoot(storageRoot).TestObject;
                Account1 = Build.An.Account.WithBalance(2).WithCode(Code1).WithStorageRoot(storageRoot).TestObject;
                Account2 = Build.An.Account.WithBalance(3).WithCode(Code2).WithStorageRoot(storageRoot).TestObject;
                Account3 = Build.An.Account.WithBalance(4).WithCode(Code3).WithStorageRoot(storageRoot).TestObject;

                AccountJustState0 = Build.An.Account.WithBalance(1).TestObject;
                AccountJustState1 = Build.An.Account.WithBalance(2).TestObject;
                AccountJustState2 = Build.An.Account.WithBalance(3).TestObject;

                Inited = true;
            }
        }

        private static (string Name, Action<StateTree, ITrieStore, IDb> Action)[]? _scenarios;

        public static (string Name, Action<StateTree, ITrieStore, IDb> Action)[] Scenarios
            => LazyInitializer.EnsureInitialized(ref _scenarios, InitScenarios);

        // FlatDB stores 160-bit account-key prefixes. Keep the deliberately shared prefixes shorter than that;
        // the full-width path collision cases belong in the raw trie tests.
        private static (string Name, Action<StateTree, ITrieStore, IDb> Action)[] InitScenarios() =>
        [
            ("empty", static (tree, _, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                tree.Commit();
            }),
            ("set_3_via_address", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                SetStorage(tree, stateDb, TestItem.AddressA, Account0);
                SetStorage(tree, stateDb, TestItem.AddressB, Account0);
                SetStorage(tree, stateDb, TestItem.AddressC, Account0);
                tree.Commit();
            }),
            ("storage_hash_and_code_hash_same", static (tree, stateDb, codeDb) =>
            {
                byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
                Hash256 codeHash = Keccak.Compute(code);
                Hash256 account = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                StorageTree remoteStorageTree = new(stateDb.GetTrieStore(account), Keccak.EmptyTreeHash, LimboLogs.Instance);
                remoteStorageTree.Set((UInt256) 1, new byte[] {1});
                remoteStorageTree.Commit();
                remoteStorageTree.UpdateRootHash();
                codeDb[codeHash.Bytes] = code;
                tree.Set(account, AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash).WithChangedCodeHash(codeHash));
                tree.Commit();
            }),
            ("storage_hash_and_code_hash_same_with_additional_account_of_same_storage_root", static (tree, stateDb, codeDb) =>
            {
                byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
                Hash256 codeHash = Keccak.Compute(code);

                Hash256 account1 = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                StorageTree remoteStorageTree1 = new(stateDb.GetTrieStore(account1), Keccak.EmptyTreeHash, LimboLogs.Instance);
                remoteStorageTree1.Set((UInt256) 1, new byte[] {1});
                remoteStorageTree1.Commit();
                remoteStorageTree1.UpdateRootHash();

                Hash256 account2 = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                StorageTree remoteStorageTree2 = new(stateDb.GetTrieStore(account2), Keccak.EmptyTreeHash, LimboLogs.Instance);
                remoteStorageTree2.Set((UInt256) 1, new byte[] {1});
                remoteStorageTree2.Commit();
                remoteStorageTree2.UpdateRootHash();

                codeDb[codeHash.Bytes] = code;
                tree.Set(account1, AccountJustState0.WithChangedStorageRoot(remoteStorageTree1.RootHash));
                tree.Set(account2, AccountJustState0.WithChangedStorageRoot(remoteStorageTree2.RootHash).WithChangedCodeHash(codeHash));
                tree.Commit();
            }),
            ("storage_hash_and_code_hash_same_with_additional_account_of_same_code", static (tree, stateDb, codeDb) =>
            {
                byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
                Hash256 codeHash = Keccak.Compute(code);

                Hash256 accountWithStorage = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                StorageTree remoteStorageTree = new(stateDb.GetTrieStore(accountWithStorage), Keccak.EmptyTreeHash, LimboLogs.Instance);
                remoteStorageTree.Set((UInt256) 1, new byte[] {1});
                remoteStorageTree.Commit();
                remoteStorageTree.UpdateRootHash();

                codeDb[codeHash.Bytes] = code;
                tree.Set(new Hash256("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0.WithChangedCodeHash(codeHash));
                tree.Set(accountWithStorage, AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash).WithChangedCodeHash(codeHash));
                tree.Commit();
            }),
            ("branch_with_same_accounts_at_different_addresses", static (tree, _, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                tree.Set(new Hash256("1baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0);
                tree.Set(new Hash256("2baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0);
                tree.Commit();
            }),
            ("set_3_delete_1", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), null);
                tree.Commit();
            }),
            ("set_3_delete_2", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), null);
                tree.Commit();
            }),
            ("set_3_delete_all", static (tree, _, _) =>
            {
                // SetStorage(stateDb);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), null);
                tree.Commit();
            }),
            ("extension_read_full_match", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb11111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account1);
                Account _ = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb11111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"))!;
                tree.UpdateRootHash();
                Hash256 __ = tree.RootHash;
                tree.Commit();
            }),
            ("extension_read_missing", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb11111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account1);
                Account _ = tree.Get(new Hash256("eeeeeeeeddddddddddddddddddddddddeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"))!;
                tree.UpdateRootHash();
                Hash256 __ = tree.RootHash;
                tree.Commit();
            }),
            ("extension_new_branch", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb11111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account1);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeddddddddddddddddddddddddeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account2);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("just_state", static (tree, _, _) =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), AccountJustState0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb11111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), AccountJustState1);
                tree.Set(new Hash256("eeeeeeeeddddddddddddddddddddddddeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), AccountJustState2);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("extension_delete_missing", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb11111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account1);
                tree.Set(new Hash256("eeeeeeedddddddddddddddddddddddddeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), null);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("extension_create_new_extension", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                codeDb[Keccak.Compute(Code3).Bytes] = Code3;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeb11111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account1);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeaaaaaaaaaaaaaaaab00000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account2);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeaaaaaaaaaaaaaaaab11111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account3);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_new_value", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                SetStorage(tree, stateDb, new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                SetStorage(tree, stateDb, new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account1);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_no_change", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                SetStorage(tree, stateDb, new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                SetStorage(tree, stateDb, new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_delete", static (tree, _, _) =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), null);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_delete_missing", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                SetStorage(tree, stateDb, new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                tree.Set(new Hash256("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_update_extension", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                SetStorage(tree, stateDb, new Hash256("11111111111111111111111111111111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb"), Account0);
                SetStorage(tree, stateDb, new Hash256("00000000000000000000000000000000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb"), Account1);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_read", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                SetStorage(tree, stateDb, new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                Account _ = tree.Get(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"))!;
                tree.UpdateRootHash();
                Hash256 __ = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_update_missing", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                SetStorage(tree, stateDb, new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                Account _ = tree.Get(new Hash256("111111111111111111111111111111111111111111111111111111111ddddddd"))!;
                tree.UpdateRootHash();
                Hash256 __ = tree.RootHash;
                tree.Commit();
            }),
            ("branch_update_missing", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb00000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb11111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account1);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb22222eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account2);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            }),
            ("branch_read_missing", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb00000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb11111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account1);
                Account _ = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb22222eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"))!;
                tree.UpdateRootHash();
                Hash256 __ = tree.RootHash;
                tree.Commit();
            }),
            ("branch_delete_missing", static (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb00000eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account0);
                SetStorage(tree, stateDb, new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb11111eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), Account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeb22222eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), null);
                tree.UpdateRootHash();
                Hash256 _ = tree.RootHash;
                tree.Commit();
            })
        ];

        private static StorageTree SetStorage(ITrieStore trieStore, Address account) => SetStorage(trieStore, account.ToAccountPath.ToCommitment());

        private static StorageTree SetStorage(ITrieStore trieStore, Hash256 account)
        {
            StorageTree remoteStorageTree = new(trieStore.GetTrieStore(account), Keccak.EmptyTreeHash, LimboLogs.Instance);

            remoteStorageTree.Set((UInt256)1, [1]);
            remoteStorageTree.Set((UInt256)2, [2]);
            remoteStorageTree.Set((UInt256)3, [3]);
            remoteStorageTree.Set((UInt256)4, [4]);
            remoteStorageTree.Set((UInt256)1005, [5]);
            remoteStorageTree.Set((UInt256)1006, [6]);
            remoteStorageTree.Set((UInt256)1007, [7]);
            remoteStorageTree.Set((UInt256)1008, [8]);

            remoteStorageTree.Commit();
            return remoteStorageTree;
        }

        private static void SetStorage(StateTree stateTree, ITrieStore trieStore, Address address, Account account) =>
            SetStorage(stateTree, trieStore, address.ToAccountPath.ToCommitment(), account);

        private static void SetStorage(StateTree stateTree, ITrieStore trieStore, Hash256 address, Account account)
        {
            SetStorage(trieStore, address);
            stateTree.Set(address, account);
        }
    }
}
