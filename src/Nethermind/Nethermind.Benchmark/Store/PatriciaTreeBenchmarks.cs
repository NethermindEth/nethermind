// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Benchmarks.Store
{

    [MemoryDiagnoser]
    public class PatriciaTreeBenchmarks
    {
        private static readonly Account _empty = Build.An.Account.WithBalance(0).TestObject;
        private static readonly Account _account0 = Build.An.Account.WithBalance(1).TestObject;
        private static readonly Account _account1 = Build.An.Account.WithBalance(2).TestObject;
        private static readonly Account _account2 = Build.An.Account.WithBalance(3).TestObject;
        private static readonly Account _account3 = Build.An.Account.WithBalance(4).TestObject;

        private StateTree _tree;

        private Hash256 _rootHash;

        // Just the backing KV. Used for benchmarking that include deserialization overhead.
        private MemDb _backingMemory;

        // Full uncommitted tree with in memory node. Node should be fully deserialized.
        private StateTree _uncommittedFullTree;

        private StateTree _fullTree;

        private TrieStore _memoryTrieStore;

        // All entries
        private const int _entryCount = 1024 * 4;
        private (Hash256, Account)[] _entries;
        private (Hash256, Account)[] _entriesShuffled;

        private const int _largerEntryCount = 1024 * 10 * 10;
        private (bool, Hash256, Account)[] _largerEntriesAccess;

        private (string Name, Action<StateTree> Action)[] _scenarios = new (string, Action<StateTree>)[]
        {
            ("set_3_via_address", tree =>
            {
                tree.Set(TestItem.AddressA, _account0);
                tree.Set(TestItem.AddressB, _account0);
                tree.Set(TestItem.AddressC, _account0);
                tree.Commit();
            }),
            ("set_3_via_hash", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Commit();
            }),
            ("set_3_delete_1", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Commit();
            }),
            ("set_3_delete_2", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Commit();
            }),
            ("set_3_delete_all", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
                tree.Commit();
            }),
            ("extension_read_full_match", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("extension_read_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("extension_new_branch", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), _account2);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("extension_delete_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("extenson_create_new_extension", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), _account2);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), _account3);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_new_value", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account1);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_no_change", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_delete", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), null);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_delete_missing", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_update_extension", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), _account1);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_read", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                Account account = tree.Get(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_update_missing", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                Account account = tree.Get(new Hash256("111111111111111111111111111111111111111111111111111111111ddddddd"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("branch_update_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), _account2);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("branch_read_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("branch_delete_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit();
            }),
        };

        [GlobalSetup]
        public void Setup()
        {
            _tree = new StateTree();

            _entries = new (Hash256, Account)[_entryCount];
            for (int i = 0; i < _entryCount; i++)
            {
                _entries[i] = (Keccak.Compute(i.ToBigEndianByteArray()), new Account((UInt256)i));
            }

            _entriesShuffled = new (Hash256, Account)[_entryCount];
            for (int i = 0; i < _entryCount; i++)
            {
                _entriesShuffled[i] = _entries[i];
            }
            new Random(0).Shuffle(_entriesShuffled);

            _backingMemory = new MemDb();
            StateTree tempTree = new StateTree(new RawScopedTrieStore(new NodeStorage(_backingMemory), null), NullLogManager.Instance);
            for (int i = 0; i < _entryCount; i++)
            {
                tempTree.Set(_entries[i].Item1, _entries[i].Item2);
            }
            tempTree.Commit();
            _rootHash = tempTree.RootHash;

            _fullTree = new StateTree();
            for (int i = 0; i < _entryCount; i++)
            {
                _fullTree.Set(_entries[i].Item1, _entries[i].Item2);
            }
            _fullTree.Commit();

            _uncommittedFullTree = new StateTree();
            for (int i = 0; i < _entryCount; i++)
            {
                _uncommittedFullTree.Set(_entries[i].Item1, _entries[i].Item2);
            }

            _memoryTrieStore = TestTrieStoreFactory.Build(_backingMemory, Prune.WhenCacheReaches(1.GB()), No.Persistence, NullLogManager.Instance);

            // Preparing access for large entries
            List<Hash256> currentItems = new();

            _largerEntriesAccess = new (bool, Hash256, Account)[_largerEntryCount];
            Random rand = new Random(0);
            for (int i = 0; i < _largerEntryCount; i++)
            {
                if (rand.NextDouble() < 0.4 && currentItems.Count != 0)
                {
                    // Its an existing read
                    _largerEntriesAccess[i] = (
                        false,
                        currentItems[(int)(rand.NextInt64() % currentItems.Count)],
                        Account.TotallyEmpty);
                }
                else if (rand.NextDouble() < 0.6 && currentItems.Count != 0)
                {
                    // Its an existing write
                    _largerEntriesAccess[i] = (
                        true,
                        currentItems[(int)(rand.NextInt64() % currentItems.Count)],
                        new Account((UInt256)rand.NextInt64()));
                }
                else
                {
                    // Its a new write
                    Hash256 newAccount = Keccak.Compute(i.ToBigEndianByteArray());
                    currentItems.Add(newAccount);
                    _largerEntriesAccess[i] = (
                        true,
                        newAccount,
                        new Account((UInt256)rand.NextInt64()));
                }
            }

        }

        [Benchmark]
        public void Scenarios()
        {
            for (int i = 0; i < 19; i++)
            {
                _scenarios[i].Action(_tree);
            }
        }

        [Benchmark]
        public void InsertAndHash()
        {
            StateTree tempTree = new StateTree();
            for (int i = 0; i < _entryCount; i++)
            {
                tempTree.Set(_entries[i].Item1, _entries[i].Item2);
            }
            tempTree.UpdateRootHash();
        }

        [Benchmark]
        public void InsertAndCommit()
        {
            StateTree tempTree = new StateTree(new RawScopedTrieStore(new MemDb()), NullLogManager.Instance);
            for (int i = 0; i < _entryCount; i++)
            {
                tempTree.Set(_entries[i].Item1, _entries[i].Item2);
            }
            tempTree.Commit();
        }

        [Benchmark]
        public void InsertAndCommitRepeatedlyTimes()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);

            for (int i = 0; i < _largerEntryCount; i++)
            {
                if (i % 2000 == 0)
                {
                    using IBlockCommitter _ = trieStore.BeginBlockCommit(i / 2000);
                    tempTree.Commit();
                }

                (bool isWrite, Hash256 address, Account value) = _largerEntriesAccess[i];
                if (isWrite)
                {
                    tempTree.Set(address, value);
                }
                else
                {
                    tempTree.Get(address);
                }
            }
        }

        [Benchmark]
        public void LargeInsertAndCommit()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);

            for (int i = 0; i < _largerEntryCount; i++)
            {
                (bool isWrite, Hash256 address, Account value) = _largerEntriesAccess[i];
                if (isWrite)
                {
                    tempTree.Set(address, value);
                }
                else
                {
                    tempTree.Get(address);
                }
            }

            using IBlockCommitter _ = trieStore.BeginBlockCommit(0);
            tempTree.Commit();
        }

        TrieStore _largeUncommittedFullTree;
        StateTree _largeUncommittedStateTree;

        [IterationSetup(Targets = [
            nameof(LargeCommit),
            nameof(LargeHash),
            nameof(LargeHashNoParallel),
        ])]
        public void SetupLargeUncommittedTree()
        {
            TrieStore trieStore = _largeUncommittedFullTree = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = _largeUncommittedStateTree = new StateTree(trieStore, NullLogManager.Instance);

            for (int i = 0; i < _largerEntryCount; i++)
            {
                (bool isWrite, Hash256 address, Account value) = _largerEntriesAccess[i];
                if (isWrite)
                {
                    tempTree.Set(address, value);
                }
                else
                {
                    tempTree.Get(address);
                }
            }
        }

        [IterationCleanup(Targets = [
            nameof(LargeCommit),
            nameof(LargeHash),
            nameof(LargeHashNoParallel),
        ])]
        public void CleanupLargeUncommittedTree()
        {
            _largeUncommittedFullTree.Dispose();
        }

        [Benchmark]
        public void LargeCommit()
        {
            using IBlockCommitter _ = _largeUncommittedFullTree.BeginBlockCommit(0);
            _largeUncommittedStateTree.Commit();
        }

        [Benchmark]
        public void LargeHash()
        {
            using IBlockCommitter _ = _largeUncommittedFullTree.BeginBlockCommit(0);
            _largeUncommittedStateTree.UpdateRootHash();
        }

        [Benchmark]
        public void LargeHashNoParallel()
        {
            using IBlockCommitter _ = _largeUncommittedFullTree.BeginBlockCommit(0);
            _largeUncommittedStateTree.UpdateRootHash(canBeParallel: false);
        }

        [Benchmark]
        public void ReadWithFullTree()
        {
            for (int i = 0; i < _entryCount; i++)
            {
                _fullTree.Get(_entriesShuffled[i].Item1);
            }
        }

        [Benchmark]
        public void ReadWithUncommittedFullTree()
        {
            for (int i = 0; i < _entryCount; i++)
            {
                _uncommittedFullTree.Get(_entriesShuffled[i].Item1);
            }
        }

        [Benchmark]
        public void ReadWithMemoryTrieStore()
        {
            StateTree tempTree = new StateTree(_memoryTrieStore, NullLogManager.Instance);
            tempTree.RootHash = _rootHash;
            for (int i = 0; i < _entryCount; i++)
            {
                tempTree.Get(_entries[i].Item1);
            }
        }

        [Benchmark]
        public void ReadWithMemoryTrieStoreReadOnly()
        {
            StateTree tempTree = new StateTree(_memoryTrieStore.AsReadOnly(), NullLogManager.Instance);
            tempTree.RootHash = _rootHash;
            for (int i = 0; i < _entryCount; i++)
            {
                tempTree.Get(_entries[i].Item1);
            }
        }

        [Benchmark]
        public void ReadAndDeserialize()
        {
            StateTree tempTree = new StateTree(new RawScopedTrieStore(_backingMemory), NullLogManager.Instance);
            tempTree.RootHash = _rootHash;
            for (int i = 0; i < _entryCount; i++)
            {
                tempTree.Get(_entriesShuffled[i].Item1);
            }
        }
    }
}
