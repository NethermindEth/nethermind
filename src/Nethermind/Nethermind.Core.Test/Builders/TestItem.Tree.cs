// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test.Builders
{
    public static partial class TestItem
    {
        public static class Tree
        {
            public static Hash256 AccountAddress0 = new Hash256("0000000000000000000000000000000000000000000000000000000001101234");

            private static readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
            private static readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;
            private static readonly Account _account2 = Build.An.Account.WithBalance(2).TestObject;
            private static readonly Account _account3 = Build.An.Account.WithBalance(3).TestObject;
            private static readonly Account _account4 = Build.An.Account.WithBalance(4).TestObject;
            private static readonly Account _account5 = Build.An.Account.WithBalance(5).TestObject;

            public static PathWithAccount[] AccountsWithPaths = new PathWithAccount[]
                {
                new PathWithAccount(AccountAddress0, _account0),
                new PathWithAccount(new Hash256("0000000000000000000000000000000000000000000000000000000001112345"), _account1),
                new PathWithAccount(new Hash256("0000000000000000000000000000000000000000000000000000000001113456"), _account2),
                new PathWithAccount(new Hash256("0000000000000000000000000000000000000000000000000000000001114567"), _account3),
                new PathWithAccount(new Hash256("0000000000000000000000000000000000000000000000000000000001123456"), _account4),
                new PathWithAccount(new Hash256("0000000000000000000000000000000000000000000000000000000001123457"), _account5),
                };

            public static PathWithStorageSlot[] SlotsWithPaths = new PathWithStorageSlot[]
            {
                new PathWithStorageSlot(new Hash256("0000000000000000000000000000000000000000000000000000000001101234"), Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")).Bytes),
                new PathWithStorageSlot(new Hash256("0000000000000000000000000000000000000000000000000000000001112345"), Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")).Bytes),
                new PathWithStorageSlot(new Hash256("0000000000000000000000000000000000000000000000000000000001113456"), Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")).Bytes),
                new PathWithStorageSlot(new Hash256("0000000000000000000000000000000000000000000000000000000001114567"), Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")).Bytes),
                new PathWithStorageSlot(new Hash256("0000000000000000000000000000000000000000000000000000000001123456"), Rlp.Encode(Bytes.FromHexString("0xab90000000000000000000000000000000000000000000000000000000000000000000000000000000")).Bytes),
                new PathWithStorageSlot(new Hash256("0000000000000000000000000000000000000000000000000000000001123457"), Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")).Bytes),
            };

            public static StateTree GetStateTree(ITrieStore? store = null)
            {
                store ??= TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);

                var stateTree = new StateTree(store.GetTrieStore(null), LimboLogs.Instance);

                FillStateTreeWithTestAccounts(stateTree);

                return stateTree;
            }

            public static void FillStateTreeWithTestAccounts(StateTree stateTree)
            {
                stateTree.Set(AccountsWithPaths[0].Path, AccountsWithPaths[0].Account);
                stateTree.Set(AccountsWithPaths[1].Path, AccountsWithPaths[1].Account);
                stateTree.Set(AccountsWithPaths[2].Path, AccountsWithPaths[2].Account);
                stateTree.Set(AccountsWithPaths[3].Path, AccountsWithPaths[3].Account);
                stateTree.Set(AccountsWithPaths[4].Path, AccountsWithPaths[4].Account);
                stateTree.Set(AccountsWithPaths[5].Path, AccountsWithPaths[5].Account);
                stateTree.Commit();
            }

            public static void FillStateTreeMultipleAccount(StateTree stateTree, int accountNumber)
            {
                for (int i = 0; i < accountNumber; i++)
                {
                    Account acc = Build.An.Account.WithBalance((UInt256)i).TestObject;
                    stateTree.Set(Keccak.Compute(i.ToBigEndianByteArray()), acc);
                }
                stateTree.Commit();
            }

            public static (StateTree stateTree, StorageTree storageTree, Hash256 accountAddr) GetTrees(ITrieStore? store)
            {
                store ??= TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);

                var storageTree = new StorageTree(store.GetTrieStore(AccountAddress0), LimboLogs.Instance);

                storageTree.Set(SlotsWithPaths[0].Path, SlotsWithPaths[0].SlotRlpValue, false);
                storageTree.Set(SlotsWithPaths[1].Path, SlotsWithPaths[1].SlotRlpValue, false);
                storageTree.Set(SlotsWithPaths[2].Path, SlotsWithPaths[2].SlotRlpValue, false);
                storageTree.Set(SlotsWithPaths[3].Path, SlotsWithPaths[3].SlotRlpValue, false);
                storageTree.Set(SlotsWithPaths[4].Path, SlotsWithPaths[4].SlotRlpValue, false);
                storageTree.Set(SlotsWithPaths[5].Path, SlotsWithPaths[5].SlotRlpValue, false);

                storageTree.Commit();

                var account = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;

                var stateTree = new StateTree(store.GetTrieStore(null), LimboLogs.Instance);
                stateTree.Set(AccountAddress0, account);
                stateTree.Commit();

                return (stateTree, storageTree, AccountAddress0);
            }

            public static (StateTree stateTree, StorageTree storageTree, Hash256 accountAddr) GetTrees(ITrieStore? store, int slotNumber)
            {
                store ??= TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);

                var storageTree = new StorageTree(store.GetTrieStore(AccountAddress0), LimboLogs.Instance);

                for (int i = 0; i < slotNumber; i++)
                {
                    storageTree.Set(
                        Keccak.Compute(i.ToBigEndianByteArray()),
                        Keccak.Compute(i.ToBigEndianByteArray()).BytesToArray(),
                        false);
                }

                storageTree.Commit();

                var account = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;

                var stateTree = new StateTree(store, LimboLogs.Instance);
                stateTree.Set(AccountAddress0, account);
                stateTree.Commit();

                return (stateTree, storageTree, AccountAddress0);
            }
        }
    }
}
