// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie
{
    public class TrieStatsCollector : ITreeVisitor
    {
        private readonly ClockCache<ValueHash256, int> _existingCodeHash = new ClockCache<ValueHash256, int>(1024 * 8);
        private readonly IKeyValueStore _codeKeyValueStore;
        private long _lastAccountNodeCount = 0;

        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;

        public TrieStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager, CancellationToken cancellationToken = default)
        {
            _codeKeyValueStore = codeKeyValueStore ?? throw new ArgumentNullException(nameof(codeKeyValueStore));
            _logger = logManager.GetClassLogger();
            _cancellationToken = cancellationToken;
        }

        public TrieStats Stats { get; } = new();

        public bool IsFullDbScan => true;

        public bool ShouldVisit(Hash256 nextNode)
        {
            return true;
        }

        public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext) { }

        public void VisitMissingNode(Hash256 nodeHash, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Increment(ref Stats._missingStorage);
            }
            else
            {
                Interlocked.Increment(ref Stats._missingState);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._storageBranchCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._stateBranchCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._storageExtensionCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._stateExtensionCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
        {
            long lastAccountNodeCount = _lastAccountNodeCount;
            long currentNodeCount = Stats.NodesCount;
            if (currentNodeCount - lastAccountNodeCount > 1_000_000 && Interlocked.CompareExchange(ref _lastAccountNodeCount, currentNodeCount, lastAccountNodeCount) == lastAccountNodeCount)
            {
                _logger.Warn($"Collected info from {Stats.NodesCount} nodes. Missing CODE {Stats.MissingCode} STATE {Stats.MissingState} STORAGE {Stats.MissingStorage}");
            }

            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._storageLeafCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._accountCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
            ValueHash256 key = new ValueHash256(codeHash.Bytes);
            bool codeExist = _existingCodeHash.TryGet(key, out int codeLength);
            if (!codeExist)
            {
                byte[] code = _codeKeyValueStore[codeHash.Bytes];
                codeExist = code is not null;
                if (codeExist)
                {
                    codeLength = code.Length;
                    _existingCodeHash.Set(key, codeLength);
                }
            }

            if (codeExist)
            {
                Interlocked.Add(ref Stats._codeSize, codeLength);
                Interlocked.Increment(ref Stats._codeCount);
            }
            else
            {
                Interlocked.Increment(ref Stats._missingCode);
            }

            IncrementLevel(trieVisitContext, Stats._codeLevels);
        }

        private void IncrementLevel(TrieVisitContext trieVisitContext)
        {
            long[] levels = trieVisitContext.IsStorage ? Stats._storageLevels : Stats._stateLevels;
            IncrementLevel(trieVisitContext, levels);
        }

        private static void IncrementLevel(TrieVisitContext trieVisitContext, long[] levels)
        {
            Interlocked.Increment(ref levels[trieVisitContext.Level]);
        }
    }
}
