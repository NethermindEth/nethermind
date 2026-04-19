// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store
{
    [MemoryDiagnoser]
    [MinIterationTime(1000)]
    public class PatriciaTreeSetBenchmarks
    {
        private const int _entryCount = 1024 * 10;

        [Params(2, 4, 8, 64, 512, 10240)]
        public int BatchSize { get; set; }

        [Params(false, true)]
        public bool PreSorted { get; set; }

        [Params(0, 16384)]
        public int PreloadedCount { get; set; }

        private PatriciaTree.BulkSetEntry[] _entries;
        private BlockCacheTrieStore _blockCacheStore;
        private Hash256 _preloadedRootHash;

        [GlobalSetup]
        public void Setup()
        {
            Random rng = new(0);

            _entries = new PatriciaTree.BulkSetEntry[_entryCount];
            for (int i = 0; i < _entryCount; i++)
            {
                byte[] keyBuf = new byte[32];
                rng.NextBytes(keyBuf);
                byte[] valueBuf = new byte[32];
                rng.NextBytes(valueBuf);
                _entries[i] = new PatriciaTree.BulkSetEntry(new ValueHash256(keyBuf), valueBuf);
            }

            if (PreSorted)
            {
                for (int i = 0; i < _entryCount; i += BatchSize)
                {
                    Array.Sort(_entries, i, BatchSize);
                }
            }

            MemDb backingMemDb = new();
            RawScopedTrieStore baseStore = new(backingMemDb);

            _preloadedRootHash = Keccak.EmptyTreeHash;
            if (PreloadedCount > 0)
            {
                PatriciaTree preloadTree = new(baseStore, LimboLogs.Instance);
                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> preloadSet = new(PreloadedCount);
                for (int i = 0; i < PreloadedCount; i++)
                {
                    byte[] keyBuf = new byte[32];
                    rng.NextBytes(keyBuf);
                    byte[] valueBuf = new byte[32];
                    rng.NextBytes(valueBuf);
                    preloadSet.Add(new PatriciaTree.BulkSetEntry(new ValueHash256(keyBuf), valueBuf));
                }
                preloadTree.BulkSet(preloadSet);
                preloadTree.UpdateRootHash();
                _preloadedRootHash = preloadTree.RootHash;
                preloadTree.Commit();
            }

            List<(TreePath Path, Hash256 Hash)> nodeList = [];
            if (PreloadedCount > 0)
            {
                PatriciaTree walker = new(baseStore, _preloadedRootHash, false, LimboLogs.Instance);
                walker.RootRef!.ResolveNode(baseStore, TreePath.Empty);
                BlockCacheTrieStore.CollectNodes(baseStore, walker.RootRef!, TreePath.Empty, nodeList);
                nodeList.Sort((a, b) => a.Path.CompareTo(b.Path));
            }

            Dictionary<TreePath, int> pathIndex = new(nodeList.Count);
            for (int i = 0; i < nodeList.Count; i++)
                pathIndex[nodeList[i].Path] = i;

            _blockCacheStore = new BlockCacheTrieStore(baseStore, pathIndex);
        }

        [Benchmark]
        public void RepeatedSet()
        {
            PatriciaTree tree = null;
            for (int i = 0; i < _entryCount; i++)
            {
                if (i % BatchSize == 0)
                {
                    _blockCacheStore.ResetBlockCache();
                    tree = new(_blockCacheStore, _preloadedRootHash, true, LimboLogs.Instance);
                }

                tree.Set(_entries[i].Path.BytesAsSpan, _entries[i].Value);
            }
        }

        [Benchmark]
        public void RepeatedBulkSet() => DoBulkSet(PatriciaTree.Flags.None);

        [Benchmark]
        public void RepeatedBulkSetNoParallel() => DoBulkSet(PatriciaTree.Flags.DoNotParallelize);

        private void DoBulkSet(PatriciaTree.Flags flags)
        {
            if (PreSorted) flags |= PatriciaTree.Flags.WasSorted;

            PatriciaTree tree = null;
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkSet = new(BatchSize);
            for (int i = 0; i < _entryCount; i++)
            {
                if (i % BatchSize == 0)
                {
                    if (tree is not null)
                    {
                        tree.BulkSet(bulkSet, flags);
                        bulkSet.Clear();
                    }
                    _blockCacheStore.ResetBlockCache();
                    tree = new(_blockCacheStore, _preloadedRootHash, true, LimboLogs.Instance);
                }

                bulkSet.Add(_entries[i]);
            }

            tree.BulkSet(bulkSet, flags);
        }
    }
}
