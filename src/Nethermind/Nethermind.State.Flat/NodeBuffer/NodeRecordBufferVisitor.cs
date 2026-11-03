// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>The part of <see cref="NodeRecordBufferVisitor{TNodeContext}"/> that does not depend on the node context, so it is compiled once.</summary>
internal abstract class NodeRecordBufferVisitor(
    ReadOnlySnapshotBundle bundle,
    INodeRecordBuffer nodeRecordBuffer,
    VisitingStats? diagnostics,
    ReadFlags readFlags)
{
    /// <summary>Finds a trie's root record pointer by hash in the buffer's root index, as block processing does; zero when it is not indexed.</summary>
    protected ulong FindRoot(Hash256? address, Hash256 rootHash) => nodeRecordBuffer.ResolveRoot(address, rootHash.ValueHash256).Pointer;

    protected TrieNode RootReference(Hash256 hash)
    {
        RecordLookup(TreePath.Empty);
        return new TrieNode(NodeType.Unknown, hash);
    }

    /// <summary>Decodes the node <paramref name="reference"/> names from the record at <paramref name="pointer"/>, otherwise from its RLP, embedded in the parent or loaded by path from persistence.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <returns>False when persistence does not hold it either.</returns>
    protected bool TryLoad(TrieNode reference, ulong pointer, Hash256? address, in TreePath path, out Resolved resolved)
    {
        resolved = default;
        TrieNode node = reference;
        NodeRecord? record = null;
        if (pointer != 0 && nodeRecordBuffer.TryLocate(pointer, out NodeRecord located))
        {
            record = located;
            node = new TrieNode(NodeType.Unknown, reference.Keccak!, new CappedArray<byte>(located.Rlp.ToArray()));
        }
        else if (reference.FullRlp.IsNull)
        {
            diagnostics?.RecordCacheMiss();
            byte[]? rlp = address is null
                ? bundle.TryLoadStateRlp(path, reference.Keccak!, readFlags)
                : bundle.TryLoadStorageRlp(address, path, reference.Keccak!, readFlags);
            if (rlp is null) return false;
            node = new TrieNode(NodeType.Unknown, reference.Keccak!, new CappedArray<byte>(rlp));
        }

        try
        {
            node.ResolveNode(NullTrieNodeResolver.Instance, path);
        }
        catch (TrieException)
        {
            return false;
        }

        resolved = new Resolved(node, record);
        return true;
    }

    /// <returns>The child at <paramref name="index"/>, decoded when embedded in <paramref name="parent"/> and otherwise a reference to its hash; null when there is none.</returns>
    protected TrieNode? Child(TrieNode parent, in TreePath path, int index, out TreePath childPath)
    {
        childPath = path;
        parent.AppendChildPath(ref childPath, index);
        TrieNode? child = parent.GetChildWithChildPath(NullTrieNodeResolver.Instance, ref childPath, index);
        if (child?.Keccak is not null) RecordLookup(childPath);
        return child;
    }

    private void RecordLookup(in TreePath path)
    {
        diagnostics?.RecordLookup();
        diagnostics?.ObserveDepth(path.Length);
    }

    /// <summary>A decoded node, with the record it was decoded from unless it was loaded from persistence or embedded in its parent.</summary>
    protected readonly record struct Resolved(TrieNode Node, NodeRecord? Record)
    {
        /// <summary>The record pointer of the hashed child at <paramref name="index"/>; zero when unknown, which loads the child from persistence.</summary>
        public ulong ChildPointer(int index) => Record is { } nodeRecord ? nodeRecord.View.Child(index).Pointer : 0;
    }
}

/// <summary>Runs a tree visitor over the tries of a <see cref="ReadOnlySnapshotBundle"/> by following trie node buffer record pointers.</summary>
/// <remarks>
/// Calls the visitor as <see cref="PatriciaTree.Accept{TNodeContext}"/> does. Each trie starts at the root record block processing finds
/// by hash in the buffer's root index, and each child at the record its parent points at. A node no pointer resolves, and every node below
/// it, is loaded by path from the persisted snapshots and persistence instead.
/// Records are borrowed under the caller's pins, so every parallel worker joins before <see cref="Accept"/> returns.
/// </remarks>
/// <param name="nodeRecordBuffer">The buffer block processing reads the bundle's tries from: the global one, or an overridable world scope's overlay.</param>
internal sealed class NodeRecordBufferVisitor<TNodeContext>(
    ReadOnlySnapshotBundle bundle,
    INodeRecordBuffer nodeRecordBuffer,
    ITreeVisitor<TNodeContext> visitor,
    VisitingOptions options,
    VisitingStats? diagnostics)
    : NodeRecordBufferVisitor(bundle, nodeRecordBuffer, diagnostics, visitor.ExtraReadFlag | (visitor.IsFullDbScan ? ReadFlags.HintReadAhead : ReadFlags.None))
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    private const long SubtreeSizeThreshold = 256 * 1024;
    private readonly ConcurrencyController _workers = new(options.MaxDegreeOfParallelism);

    public void Accept(Hash256 rootHash)
    {
        if (!visitor.IsFullDbScan) visitor.VisitTree(default, rootHash);
        if (rootHash == Keccak.EmptyTreeHash)
        {
            if (visitor.IsFullDbScan) visitor.VisitTree(default, rootHash);
            return;
        }

        if (!TryResolve(RootReference(rootHash), FindRoot(null, rootHash), null, TreePath.Empty, default, out Resolved root)) return;

        if (visitor.IsFullDbScan) visitor.VisitTree(default, rootHash);
        Visit(root, default, null, TreePath.Empty, long.MaxValue);
    }

    /// <summary>Loads a node as <see cref="NodeRecordBufferVisitor.TryLoad"/> does, visiting it as missing when that fails.</summary>
    private bool TryResolve(TrieNode reference, ulong pointer, Hash256? address, in TreePath path, in TNodeContext context, out Resolved resolved)
    {
        if (TryLoad(reference, pointer, address, path, out resolved)) return true;

        visitor.VisitMissingNode(context, reference.Keccak);
        return false;
    }

    private long Descend(TrieNode reference, ulong pointer, in TNodeContext context, Hash256? address, in TreePath path, long sizeHint) =>
        TryResolve(reference, pointer, address, path, context, out Resolved node)
            ? Visit(node, context, address, path, sizeHint)
            : 1;

    private long Visit(in Resolved resolved, in TNodeContext context, Hash256? address, TreePath path, long sizeHint)
    {
        TrieNode node = resolved.Node;
        long size = 1;
        sizeHint--;
        switch (node.NodeType)
        {
            case NodeType.Branch:
                visitor.VisitBranch(context, node);
                size += VisitBranch(resolved, context, address, path, sizeHint);
                break;
            case NodeType.Extension:
                visitor.VisitExtension(context, node);
                TrieNode child = Child(node, path, 0, out TreePath childPath)
                    ?? throw new InvalidDataException($"Child of an extension {node.Key} should not be null.");
                TNodeContext childContext = context.Add(node.Key!);
                if (visitor.ShouldVisit(childContext, child.Keccak!))
                    size += Descend(child, resolved.ChildPointer(0), childContext, address, childPath, sizeHint);
                break;
            case NodeType.Leaf:
                visitor.VisitLeaf(context, node);
                if (address is null && visitor.ExpectAccounts) size += VisitAccount(node, context, path, sizeHint);
                break;
            default:
                throw new TrieException($"An attempt was made to visit a node {node.Keccak} of type {node.NodeType}");
        }
        return size;
    }

    private long VisitAccount(TrieNode leaf, in TNodeContext context, TreePath path, long sizeHint)
    {
        RlpReader reader = new(leaf.Value.AsSpan());
        if (!AccountDecoder.Instance.TryDecodeStruct(ref reader, out AccountStruct account))
            throw new InvalidDataException("Non storage leaf should be an account");

        TNodeContext leafContext = context.Add(leaf.Key!);
        visitor.VisitAccount(leafContext, leaf, account);
        if (!account.HasStorage || !visitor.ShouldVisit(leafContext, account.StorageRoot)) return 0;

        path.AppendMut(leaf.Key!);
        Hash256 address = path.Path.ToCommitment();
        Hash256 storageRootHash = account.StorageRoot.ToCommitment();
        return TryResolve(RootReference(storageRootHash), FindRoot(address, storageRootHash), address, TreePath.Empty, leafContext, out Resolved storageRoot)
            ? Visit(storageRoot, leafContext.AddStorage(address), address, TreePath.Empty, sizeHint)
            : 0;
    }

    private long VisitBranch(in Resolved resolved, in TNodeContext context, Hash256? address, TreePath path, long sizeHint)
    {
        sizeHint /= TrieNode.BranchesCount;
        // As in TrieNodeTraverser, fanning out only over the top state levels and the top storage level.
        bool parallel = options.MaxDegreeOfParallelism != 1 && path.Length <= (address is null ? 2 : 1);
        List<Task<long>>? tasks = null;
        long size = 0;
        try
        {
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TrieNode? child = Child(resolved.Node, path, i, out TreePath childPath);
                if (child is null) continue;
                TNodeContext childContext = context.Add((byte)i);
                if (!visitor.ShouldVisit(childContext, child.Keccak!)) continue;

                ulong pointer = resolved.ChildPointer(i);
                if (parallel && sizeHint > SubtreeSizeThreshold && _workers.TryTakeSlot(out ConcurrencyController.Slot slot))
                {
                    tasks ??= [];
                    long childSizeHint = sizeHint;
                    tasks.Add(Task.Run(() =>
                    {
                        using ConcurrencyController.Slot worker = slot;
                        return Descend(child, pointer, childContext, address, childPath, childSizeHint);
                    }));
                }
                else
                {
                    // Changing the size hint mid-iteration is deliberate, as in TrieNodeTraverser.
                    sizeHint = Descend(child, pointer, childContext, address, childPath, sizeHint);
                    size += sizeHint;
                }
            }
        }
        finally
        {
            // Borrowed buffer records must outlive every worker, even when a callback on this thread fails.
            if (tasks is not null)
            {
                foreach (long childSize in Task.WhenAll(tasks).GetAwaiter().GetResult()) size += childSize;
            }
        }
        return size;
    }
}
