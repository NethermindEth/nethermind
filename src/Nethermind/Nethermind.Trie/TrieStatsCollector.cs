// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie
{
    public class TrieStatsCollector : ITreeVisitor
    {
        private readonly IKeyValueStore _codeKeyValueStore;
        private int _lastAccountNodeCount = 0;

        private readonly ILogger _logger;

        public TrieStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager)
        {
            _codeKeyValueStore = codeKeyValueStore ?? throw new ArgumentNullException(nameof(codeKeyValueStore));
            _logger = logManager.GetClassLogger();
        }

        public TrieStats Stats { get; } = new();

        public bool IsFullDbScan => true;

        public bool ShouldVisit(Keccak nextNode)
        {
            return true;
        }

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext) { }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
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
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._storageBranchCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._stateBranchCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._storageExtensionCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._stateExtensionCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
        {
            if (Stats.NodesCount - _lastAccountNodeCount > 1_000_000)
            {
                _lastAccountNodeCount = Stats.NodesCount;
                _logger.Warn($"Collected info from {Stats.NodesCount} nodes. Missing CODE {Stats.MissingCode} STATE {Stats.MissingState} STORAGE {Stats.MissingStorage}");
            }

            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._storageLeafCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._accountCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
            byte[] code = _codeKeyValueStore[codeHash.Bytes];
            if (code is not null)
            {
                Interlocked.Add(ref Stats._codeSize, code.Length);
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
            int[] levels = trieVisitContext.IsStorage ? Stats._storageLevels : Stats._stateLevels;
            IncrementLevel(trieVisitContext, levels);
        }

        private static void IncrementLevel(TrieVisitContext trieVisitContext, int[] levels)
        {
            Interlocked.Increment(ref levels[trieVisitContext.Level]);
        }
    }
}
