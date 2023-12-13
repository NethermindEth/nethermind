// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
                StorageTree remoteStorageTree = SetStorage(new TrieStore(new MemDb(), LimboLogs.Instance));
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

        private static (string Name, Action<StateTree, ITrieStore, IDb> Action)[] InitScenarios()
        {
            return new (string, Action<StateTree, ITrieStore, IDb>)[]
            {
                ("empty", (tree, _, codeDb) =>
                {
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Commit(0);
                }),
                ("set_3_via_address", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(TestItem.AddressA, Account0);
                    tree.Set(TestItem.AddressB, Account0);
                    tree.Set(TestItem.AddressC, Account0);
                    tree.Commit(0);
                }),
                ("storage_hash_and_code_hash_same", (tree, stateDb, codeDb) =>
                {
                    byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
                    Hash256 codeHash = Keccak.Compute(code);
                    StorageTree remoteStorageTree = new(stateDb, Keccak.EmptyTreeHash, LimboLogs.Instance);
                    remoteStorageTree.Set((UInt256) 1, new byte[] {1});
                    remoteStorageTree.Commit(0);
                    remoteStorageTree.UpdateRootHash();
                    codeDb[codeHash.Bytes] = code;
                    tree.Set(new Hash256("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash).WithChangedCodeHash(codeHash));
                    tree.Commit(0);
                }),
                ("storage_hash_and_code_hash_same_with_additional_account_of_same_storage_root", (tree, stateDb, codeDb) =>
                {
                    byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
                    Hash256 codeHash = Keccak.Compute(code);
                    StorageTree remoteStorageTree = new(stateDb, Keccak.EmptyTreeHash, LimboLogs.Instance);
                    remoteStorageTree.Set((UInt256) 1, new byte[] {1});
                    remoteStorageTree.Commit(0);
                    remoteStorageTree.UpdateRootHash();
                    codeDb[codeHash.Bytes] = code;
                    tree.Set(new Hash256("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
                    tree.Set(new Hash256("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash).WithChangedCodeHash(codeHash));
                    tree.Commit(0);
                }),
                ("storage_hash_and_code_hash_same_with_additional_account_of_same_code", (tree, stateDb, codeDb) =>
                {
                    byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
                    Hash256 codeHash = Keccak.Compute(code);
                    StorageTree remoteStorageTree = new(stateDb, Keccak.EmptyTreeHash, LimboLogs.Instance);
                    remoteStorageTree.Set((UInt256) 1, new byte[] {1});
                    remoteStorageTree.Commit(0);
                    remoteStorageTree.UpdateRootHash();
                    codeDb[codeHash.Bytes] = code;
                    tree.Set(new Hash256("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0.WithChangedCodeHash(codeHash));
                    tree.Set(new Hash256("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash).WithChangedCodeHash(codeHash));
                    tree.Commit(0);
                }),
                ("branch_with_same_accounts_at_different_addresses", (tree, _, codeDb) =>
                {
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Hash256("1baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0);
                    tree.Set(new Hash256("2baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0);
                    tree.Commit(0);
                }),
                ("set_3_delete_1", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                    tree.Commit(0);
                }),
                ("set_3_delete_2", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                    tree.Commit(0);
                }),
                ("set_3_delete_all", (tree, _, _) =>
                {
//                    SetStorage(stateDb);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
                    tree.Commit(0);
                }),
                ("extension_read_full_match", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    Account _ = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"))!;
                    tree.UpdateRootHash();
                    Hash256 __ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("extension_read_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    Account _ = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"))!;
                    tree.UpdateRootHash();
                    Hash256 __ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("extension_new_branch", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), Account2);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("just_state", (tree, _, _) =>
                {
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), AccountJustState0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), AccountJustState1);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), AccountJustState2);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("extension_delete_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("extension_create_new_extension", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                    codeDb[Keccak.Compute(Code3).Bytes] = Code3;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), Account2);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), Account3);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("leaf_new_value", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account1);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("leaf_no_change", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("leaf_delete", (tree, _, _) =>
                {
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), null);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("leaf_delete_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.Set(new Hash256("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("leaf_update_extension", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), Account1);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("leaf_read", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    Account _ = tree.Get(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"))!;
                    tree.UpdateRootHash();
                    Hash256 __ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("leaf_update_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    Account _ = tree.Get(new Hash256("111111111111111111111111111111111111111111111111111111111ddddddd"))!;
                    tree.UpdateRootHash();
                    Hash256 __ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("branch_update_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), Account1);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), Account2);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("branch_read_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), Account1);
                    Account _ = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"))!;
                    tree.UpdateRootHash();
                    Hash256 __ = tree.RootHash;
                    tree.Commit(0);
                }),
                ("branch_delete_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), Account0);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), Account1);
                    tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
                    tree.UpdateRootHash();
                    Hash256 _ = tree.RootHash;
                    tree.Commit(0);
                })
            };
        }

        private static StorageTree SetStorage(ITrieStore trieStore)
        {
            StorageTree remoteStorageTree = new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);

            remoteStorageTree.Set((UInt256)1, new byte[] { 1 });
            remoteStorageTree.Set((UInt256)2, new byte[] { 2 });
            remoteStorageTree.Set((UInt256)3, new byte[] { 3 });
            remoteStorageTree.Set((UInt256)4, new byte[] { 4 });
            remoteStorageTree.Set((UInt256)1005, new byte[] { 5 });
            remoteStorageTree.Set((UInt256)1006, new byte[] { 6 });
            remoteStorageTree.Set((UInt256)1007, new byte[] { 7 });
            remoteStorageTree.Set((UInt256)1008, new byte[] { 8 });

            remoteStorageTree.Commit(0);
            return remoteStorageTree;
        }
    }
}
