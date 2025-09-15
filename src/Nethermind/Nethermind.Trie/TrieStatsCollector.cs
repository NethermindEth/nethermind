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
    public class TrieStatsCollector : ITreeVisitor<TrieStatsCollector.Context>
    {
        private readonly ClockCache<ValueHash256, int> _existingCodeHash = new ClockCache<ValueHash256, int>(1024 * 8);
        private readonly IKeyValueStore _codeKeyValueStore;
        private long _lastAccountNodeCount = 0;
        private readonly ProgressLogger _progressLogger;

        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;

        // Combine both `TreePathContextWithStorage` and `OldStyleTrieVisitContext`
        public struct Context : INodeContext<Context>
        {
            private TreePathContextWithStorage PathContext;
            private OldStyleTrieVisitContext OldStyleTrieVisitContext;

            public readonly Hash256? Storage => PathContext.Storage;
            public readonly TreePath Path => PathContext.Path;
            public readonly bool IsStorage => OldStyleTrieVisitContext.IsStorage;
            public readonly int Level => OldStyleTrieVisitContext.Level;

            public readonly Context Add(ReadOnlySpan<byte> nibblePath)
            {
                return new Context()
                {
                    PathContext = PathContext.Add(nibblePath),
                    OldStyleTrieVisitContext = OldStyleTrieVisitContext.Add(nibblePath)
                };
            }

            public readonly Context Add(byte nibble)
            {
                return new Context()
                {
                    PathContext = PathContext.Add(nibble),
                    OldStyleTrieVisitContext = OldStyleTrieVisitContext.Add(nibble)
                };
            }

            public readonly Context AddStorage(in ValueHash256 storage)
            {
                return new Context()
                {
                    PathContext = PathContext.AddStorage(storage),
                    OldStyleTrieVisitContext = OldStyleTrieVisitContext.AddStorage(storage)
                };
            }
        }

        public TrieStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager, ProgressLogger progressLogger, CancellationToken cancellationToken = default)
        {
            _codeKeyValueStore = codeKeyValueStore ?? throw new ArgumentNullException(nameof(codeKeyValueStore));
            _logger = logManager.GetClassLogger();
            _cancellationToken = cancellationToken;
            _progressLogger = progressLogger ?? throw new ArgumentNullException(nameof(progressLogger));
        }

        public TrieStats Stats { get; } = new();

        public bool IsFullDbScan => true;
        public void VisitTree(in Context nodeContext, in ValueHash256 rootHash)
        {
            _progressLogger.Reset(0, 0); // We'll update as we go since we don't know total nodes upfront
        }

        public bool ShouldVisit(in Context nodeContext, in ValueHash256 nextNode)
        {
            return true;
        }

        public void VisitMissingNode(in Context nodeContext, in ValueHash256 nodeHash)
        {
            if (nodeContext.IsStorage)
            {
                if (_logger.IsWarn) _logger.Warn($"Missing node. Storage: {nodeContext.Storage} Path: {nodeContext.Path} Hash: {nodeHash}");
                Interlocked.Increment(ref Stats._missingStorage);
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Missing node. Path: {nodeContext.Path} Hash: {nodeHash}");
                Interlocked.Increment(ref Stats._missingState);
            }

            IncrementLevel(nodeContext);
        }

        public void VisitBranch(in Context nodeContext, TrieNode node)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (nodeContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._storageBranchCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._stateBranchCount);
            }

            IncrementLevel(nodeContext);
        }

        public void VisitExtension(in Context nodeContext, TrieNode node)
        {
            if (nodeContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._storageExtensionCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._stateExtensionCount);
            }

            IncrementLevel(nodeContext);
        }

        public void VisitLeaf(in Context nodeContext, TrieNode node)
        {
            long lastAccountNodeCount = _lastAccountNodeCount;
            long currentNodeCount = Stats.NodesCount;

            _progressLogger.Update(currentNodeCount);

            // Log progress every 1 million nodes
            if (currentNodeCount - lastAccountNodeCount > 1_000_000 && Interlocked.CompareExchange(ref _lastAccountNodeCount, currentNodeCount, lastAccountNodeCount) == lastAccountNodeCount)
            {
                _progressLogger.LogProgress();
            }

            if (nodeContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._storageLeafCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
                Interlocked.Increment(ref Stats._accountCount);
            }

            IncrementLevel(nodeContext);
        }

        public void VisitAccount(in Context nodeContext, TrieNode node, in AccountStruct account)
        {
            if (!account.HasCode) return;
            ValueHash256 key = account.CodeHash;
            bool codeExist = _existingCodeHash.TryGet(key, out int codeLength);
            if (!codeExist)
            {
                byte[] code = _codeKeyValueStore[key.Bytes];
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
                if (_logger.IsWarn) _logger.Warn($"Missing code. Hash: {account.CodeHash}");
                Interlocked.Increment(ref Stats._missingCode);
            }

            IncrementLevel(nodeContext, Stats._codeLevels);
        }

        private void IncrementLevel(Context context)
        {
            long[] levels = context.IsStorage ? Stats._storageLevels : Stats._stateLevels;
            IncrementLevel(context, levels);
        }

        private static void IncrementLevel(Context context, long[] levels)
        {
            Interlocked.Increment(ref levels[context.Level]);
        }
    }
}
