// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

/// <summary>Seeds a benchmark's empty flat database from its generated in-memory trie.</summary>
internal static class FlatBenchmarkSeed
{
    public static void Copy(MemoryNodeStorage nodes, IPersistence persistence, StateId state)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(reader.CurrentState, state, WriteFlags.DisableWAL);
        StateTree tree = new(new RawScopedTrieStore(nodes), NullLogManager.Instance);
        tree.Accept(new Writer(batch), state.StateRoot.ToCommitment(), new VisitingOptions { MaxDegreeOfParallelism = 1 });
    }

    private sealed class Writer(IPersistence.IWriteBatch batch) : ITreeVisitor<TreePathContextWithStorage>
    {
        public bool IsFullDbScan => false;
        public bool ShouldVisit(in TreePathContextWithStorage context, in ValueHash256 nextNode) => true;
        public void VisitTree(in TreePathContextWithStorage context, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in TreePathContextWithStorage context, in ValueHash256 hash) =>
            throw new InvalidOperationException($"Generated benchmark trie is missing {hash} at {context.Path}.");
        public void VisitBranch(in TreePathContextWithStorage context, TrieNode node) => Save(context, node);
        public void VisitExtension(in TreePathContextWithStorage context, TrieNode node) => Save(context, node);
        public void VisitLeaf(in TreePathContextWithStorage context, TrieNode node) => Save(context, node);
        public void VisitAccount(in TreePathContextWithStorage context, TrieNode node, in AccountStruct account) { }

        private void Save(in TreePathContextWithStorage context, TrieNode node)
        {
            if (context.Storage is { } address)
            {
                if (node.FullRlp.Length >= 32) batch.SetStorageTrieNode(address, context.Path, node.FullRlp.AsSpan());
                FlatEntryWriter.WriteStorageFlatEntries(batch, address, context.Path, node);
            }
            else
            {
                if (node.FullRlp.Length >= 32) batch.SetStateTrieNode(context.Path, node.FullRlp.AsSpan());
                FlatEntryWriter.WriteAccountFlatEntries(batch, context.Path, node);
            }
        }
    }
}
