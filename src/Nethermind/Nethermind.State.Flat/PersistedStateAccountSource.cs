// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>Reads the accounts of the persisted flat state from its state trie, one partition at a time.</summary>
/// <remarks>
/// The flat account column keys a truncated path, so the full hashed paths that history rows need come from the trie.
/// A partition is walked on the calling thread and only its subtree is descended. The caller keeps the persisted state
/// from moving while it reads, see <see cref="IFlatPersistenceCaptureHook.HoldsFlatPersist"/>.
/// </remarks>
public sealed class PersistedStateAccountSource(IPersistence persistence, ILogManager logManager) : IPivotStateSource
{
    public StateId PersistedState
    {
        get
        {
            using IPersistence.IPersistenceReader reader = persistence.CreateReader();
            return reader.CurrentState;
        }
    }

    public void ForEachAccount(in ValueHash256 stateRoot, byte partition, Action<ValueHash256, Account> sink, CancellationToken cancellationToken)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        Hash256 root = new(stateRoot);
        PatriciaTree tree = new(new PersistedStateTrieStore(reader), logManager) { RootHash = root };
        tree.Accept(new PartitionAccountVisitor(partition, sink, cancellationToken), root, new VisitingOptions { MaxDegreeOfParallelism = 1 });
    }

    private sealed class PartitionAccountVisitor(byte partition, Action<ValueHash256, Account> sink, CancellationToken cancellationToken)
        : ITreeVisitor<TreePathContextWithStorage>
    {
        private readonly AccountDecoder _decoder = AccountDecoder.Instance;

        public bool IsFullDbScan => true;
        public bool ExpectAccounts => false;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return IsInPartition(nodeContext.Path);
        }

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
            throw new TrieException($"The persisted state trie is missing node {nodeHash} at {nodeContext.Path}.");

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            // A leaf above the partition depth carries the rest of its path in its key.
            TreePath path = nodeContext.Path.Append(node.Key);
            if (path.Path.Bytes[0] != partition) return;

            RlpReader accountReader = new(node.Value.AsSpan());
            sink(path.Path, _decoder.Decode(ref accountReader)!);
        }

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }

        private bool IsInPartition(in TreePath path) => path.Length switch
        {
            0 => true,
            1 => path.Path.Bytes[0] >> 4 == partition >> 4,
            _ => path.Path.Bytes[0] == partition,
        };
    }

    private sealed class PersistedStateTrieStore(IPersistence.IPersistenceReader reader) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStateRlp(path, flags | ReadFlags.HintCacheMiss);
    }
}
