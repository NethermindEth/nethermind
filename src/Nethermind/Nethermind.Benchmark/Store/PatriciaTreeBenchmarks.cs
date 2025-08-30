// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
using NSubstitute.Routing.Handlers;
using NUnit.Framework;

namespace Nethermind.Benchmarks.Store
{

    [MemoryDiagnoser]
    [MinIterationTime(1000)]
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

        private const int _largerEntryCount = 1024 * 10;
        private const int _repeatedlyFactor = 500;
        private (bool, Hash256, Account)[] _largerEntriesAccess;
        private (Hash256, Account)[] _uniqueLargeSet;
        private (Hash256, Account)[] _presortedLargeSet;

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
            _uniqueLargeSet = new (Hash256, Account)[_largerEntryCount];
            _presortedLargeSet = new (Hash256, Account)[_largerEntryCount];
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

                Hash256 newAccount2 = Keccak.Compute(i.ToBigEndianByteArray());
                currentItems.Add(newAccount2);
                _uniqueLargeSet[i] = (
                    newAccount2,
                    new Account((UInt256)rand.NextInt64()));
                _presortedLargeSet[i] = _uniqueLargeSet[i];
            }

            Array.Sort(_presortedLargeSet, (it1, it2) => it1.CompareTo(it2));
        }

        /*

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
                if (i % _repeatedlyFactor == 0)
                {
                    using IBlockCommitter _ = trieStore.BeginBlockCommit(i / _repeatedlyFactor);
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
        */

        [Benchmark]
        public void LargeSetOnly()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);
            tempTree.RootHash = Keccak.EmptyTreeHash;

            for (int i = 0; i < _largerEntryCount; i++)
            {
                (Hash256 address, Account value) = _uniqueLargeSet[i];
                tempTree.Set(address, value);
            }
        }

        [Benchmark]
        public void LargeBulkSet()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);
            tempTree.RootHash = Keccak.EmptyTreeHash;

            using ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry> bulkSet = new ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry>(_largerEntryCount);

            for (int i = 0; i < _largerEntryCount; i++)
            {
                (Hash256 address, Account account) = _uniqueLargeSet[i];
                Serialization.Rlp.Rlp rlp = account is null ? null : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Serialization.Rlp.Rlp.Encode(account);
                bulkSet.Add(new PatriciaTreeBulkSetter.BulkSetEntry(address, rlp?.Bytes));
            }

            PatriciaTreeBulkSetter.BulkSet(tempTree, bulkSet);
        }

        [Benchmark]
        public void LargeBulkSetOneStack()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);
            tempTree.RootHash = Keccak.EmptyTreeHash;

            using ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry> bulkSet = new ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry>(_largerEntryCount);

            using PatriciaTreeBulkSetter.ThreadResource threadResource = new PatriciaTreeBulkSetter.ThreadResource();
            PatriciaTreeBulkSetter setter = new PatriciaTreeBulkSetter(tempTree);
            TreePath path = TreePath.Empty;
            for (int i = 0; i < _largerEntryCount; i++)
            {
                (Hash256 address, Account account) = _uniqueLargeSet[i];
                Serialization.Rlp.Rlp rlp = account is null ? null : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Serialization.Rlp.Rlp.Encode(account);
                tempTree.RootRef = setter.BulkSetOneStack(threadResource, new PatriciaTreeBulkSetter.BulkSetEntry(address, rlp?.Bytes), ref path, tempTree.RootRef, PatriciaTreeBulkSetter.Flags.None);
            }

            PatriciaTreeBulkSetter.BulkSet(tempTree, bulkSet);
        }

        [Benchmark]
        public void LargeBulkSetNoParallel()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);
            tempTree.RootHash = Keccak.EmptyTreeHash;

            using ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry> bulkSet = new ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry>(_largerEntryCount);

            for (int i = 0; i < _largerEntryCount; i++)
            {
                (Hash256 address, Account account) = _uniqueLargeSet[i];
                Serialization.Rlp.Rlp rlp = account is null ? null : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Serialization.Rlp.Rlp.Encode(account);
                bulkSet.Add(new PatriciaTreeBulkSetter.BulkSetEntry(address, rlp?.Bytes));
            }

            PatriciaTreeBulkSetter.BulkSet(tempTree, bulkSet, PatriciaTreeBulkSetter.Flags.DoNotParallelize);
        }

        [Benchmark]
        public void LargeBulkSetPreSorted()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);
            tempTree.RootHash = Keccak.EmptyTreeHash;

            using ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry> bulkSet = new ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry>(_largerEntryCount);

            for (int i = 0; i < _largerEntryCount; i++)
            {
                (Hash256 address, Account account) = _presortedLargeSet[i];
                Serialization.Rlp.Rlp rlp = account is null ? null : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Serialization.Rlp.Rlp.Encode(account);
                bulkSet.Add(new PatriciaTreeBulkSetter.BulkSetEntry(address, rlp?.Bytes));
            }

            PatriciaTreeBulkSetter.BulkSet(tempTree, bulkSet, PatriciaTreeBulkSetter.Flags.WasSorted);
        }

        /*
        [Benchmark]
        public void LargeBulkSetOneByOne1()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 1;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne2()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 2;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne4()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 4;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne8()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 8;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne16()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 16;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne32()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 32;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne64()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 64;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne128()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 128;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne256()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 256;
            LargeBulkSet();
        }

        [Benchmark]
        public void LargeBulkSetOneByOne512()
        {
            PatriciaTreeBulkSetter.MinEntriesToSetOneByOne = 512;
            LargeBulkSet();
        }
        */

        /*
        [Benchmark]
        public void RepeatedSet16()
        {
            DoSetOnlyRepeatedly(16);
        }

        [Benchmark]
        public void RepeatedSet32()
        {
            DoSetOnlyRepeatedly(32);
        }

        [Benchmark]
        public void RepeatedSet64()
        {
            DoSetOnlyRepeatedly(64);
        }
        [Benchmark]
        public void RepeatedSet128()
        {
            DoSetOnlyRepeatedly(128);
        }

        [Benchmark]
        public void RepeatedSet256()
        {
            DoSetOnlyRepeatedly(256);
        }

        [Benchmark]
        public void RepeatedSet512()
        {
            DoSetOnlyRepeatedly(512);
        }

        [Benchmark]
        public void RepeatedBulkSet16()
        {
            DoBulkSetRepeatedly(16);
        }
        [Benchmark]
        public void RepeatedBulkSet32()
        {
            DoBulkSetRepeatedly(32);
        }
        [Benchmark]
        public void RepeatedBulkSet64()
        {
            DoBulkSetRepeatedly(64);
        }

        [Benchmark]
        public void RepeatedBulkSet128()
        {
            DoBulkSetRepeatedly(128);
        }

        [Benchmark]
        public void RepeatedBulkSet256()
        {
            DoBulkSetRepeatedly(256);
        }

        [Benchmark]
        public void RepeatedBulkSet512()
        {
            DoBulkSetRepeatedly(512);
        }

        public void DoSetOnlyRepeatedly(int repeatBatchSize)
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);
            var originalRootHash = Keccak.EmptyTreeHash;
            tempTree.RootHash = Keccak.EmptyTreeHash;

            for (int i = 0; i < _largerEntryCount; i++)
            {
                if (i % repeatBatchSize == 0)
                {
                    tempTree.RootHash = originalRootHash;
                }

                (Hash256 address, Account value) = _uniqueLargeSet[i];
                tempTree.Set(address, value);
            }
        }

        public void DoBulkSetRepeatedly(int repeatBatchSize)
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB()),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new StateTree(trieStore, NullLogManager.Instance);
            var originalRootHash = Keccak.EmptyTreeHash;
            tempTree.RootHash = Keccak.EmptyTreeHash;

            using ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry> bulkSet = new ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry>(_repeatedlyFactor);
            for (int i = 0; i < _largerEntryCount; i++)
            {
                if (i % repeatBatchSize == 0)
                {
                    PatriciaTreeBulkSetter.BulkSet(tempTree, bulkSet.AsMemory(), PatriciaTreeBulkSetter.Flags.None);
                    bulkSet.Clear();
                    tempTree.RootHash = originalRootHash;
                }

                (Hash256 address, Account account) = _uniqueLargeSet[i];
                Serialization.Rlp.Rlp rlp = account is null ? null : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Serialization.Rlp.Rlp.Encode(account);
                bulkSet.Add(new PatriciaTreeBulkSetter.BulkSetEntry(address, rlp?.Bytes));
            }

            PatriciaTreeBulkSetter.BulkSet(tempTree, bulkSet.AsMemory(), PatriciaTreeBulkSetter.Flags.None);
        }

        /*
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
        */
    }
}
