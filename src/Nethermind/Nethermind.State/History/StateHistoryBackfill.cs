// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.History
{
    /// <summary>
    /// Βήμα 3 — populate an <see cref="IStateHistory"/> from existing trie state by walking it (modelled on
    /// <c>Nethermind.State.Flat.Importer</c>'s visitor). The hashes come straight off the trie leaves —
    /// no preimage DB needed — which is the whole point of hash-keying the store.
    ///
    /// PROTOTYPE SCOPE: this ingests a FULL snapshot of state at one block (every leaf, tagged with that
    /// block number). That is enough for the go/no-go measurement (read latency vs trie, flat leaf size vs
    /// trie nodes). The PRODUCTION backfill is an incremental dual-trie diff — walk trie(N) vs trie(N-1)
    /// and emit only changed leaves per block — which the single-tree visitor here does not do. Bounded by
    /// <c>maxLeaves</c> so a run can sample a fraction instead of the whole 250M-account state.
    /// </summary>
    public sealed class StateHistoryBackfill(
        IStateHistory target,
        long blockNumber,
        int batchSize = 100_000,
        long maxLeaves = long.MaxValue,
        int sampleEvery = 64,
        bool traverseStorage = true,
        Action<long>? onProgress = null) : ITreeVisitor<TreePathContextWithStorage>
    {
        private readonly List<StateChange> _batch = new(batchSize);
        private long _collected;
        private long _accountCount;
        private long _missing;

        public long Collected => _collected;
        public long MissingNodes => _missing;

        /// <summary>A sample of ingested account path-hashes, for the verify/benchmark loop (capped).</summary>
        public List<ValueHash256> SampledAccountHashes { get; } = new(8192);

        /// <summary>Walks the state at <paramref name="header"/> single-threaded and ingests every leaf.</summary>
        public void Run(IStateReader reader, BlockHeader header)
        {
            // Single-threaded so the (non-thread-safe) batch list and Ingest stay simple for the prototype.
            reader.RunTreeVisitor(this, header, new VisitingOptions { MaxDegreeOfParallelism = 1 });
            Flush();
        }

        public bool IsFullDbScan => true;
        public bool ExpectAccounts => traverseStorage;   // false = account leaves only (skip storage tries)

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => _collected < maxLeaves;

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }

        // Count missing nodes instead of throwing, so the walk completes and we can see the scale
        // (1 missing = edge case; everything missing = the store/scheme/approach is wrong).
        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) => _missing++;

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            ValueHash256 fullPath = nodeContext.Path.Append(node.Key).Path;
            if (nodeContext.Storage is null)
            {
                // account leaf: value is the account RLP
                if (_accountCount++ % sampleEvery == 0 && SampledAccountHashes.Count < 8192)
                    SampledAccountHashes.Add(fullPath);
                Add(StateChange.Account(fullPath, node.Value.AsSpan().ToArray()));
            }
            else
            {
                // storage leaf: value is RLP-trimmed and never empty (zero slots are absent in the trie)
                System.ReadOnlySpan<byte> value = node.Value.AsSpan();
                if (!value.IsEmpty)
                {
                    ValueHash256 accountHash = new(nodeContext.Storage.Bytes);
                    Add(StateChange.Storage(accountHash, fullPath, value.ToArray()));
                }
            }
        }

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }

        private void Add(StateChange change)
        {
            _batch.Add(change);
            _collected++;
            if (onProgress is not null && _collected % 1000 == 0) onProgress(_collected);
            if (_batch.Count >= batchSize) Flush();
        }

        private void Flush()
        {
            if (_batch.Count == 0) return;
            target.Ingest(blockNumber, _batch);
            _batch.Clear();
        }
    }
}
