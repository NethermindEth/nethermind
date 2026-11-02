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

/// <summary>Runs a tree visitor over the tries of a <see cref="ReadOnlySnapshotBundle"/> by following trie node buffer record pointers.</summary>
/// <remarks>
/// Calls the visitor as <see cref="PatriciaTree.Accept{TNodeContext}"/> does. Each trie starts at the root record of the newest in-memory
/// snapshot holding it, and each child at the record its parent points at. A node no pointer resolves, and every node below it, is loaded
/// by path from the persisted snapshots and persistence instead.
/// Records are borrowed under the bundle's pins, so every parallel worker joins before <see cref="Accept"/> returns.
/// </remarks>
internal sealed class NodeRecordBufferVisitor<TNodeContext>(
    ReadOnlySnapshotBundle bundle,
    ITreeVisitor<TNodeContext> visitor,
    VisitingOptions options,
    VisitingStats? diagnostics)
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    private const long SubtreeSizeThreshold = 256 * 1024;
    private readonly ConcurrencyController _workers = new(options.MaxDegreeOfParallelism);
    private readonly ReadFlags _readFlags = visitor.ExtraReadFlag | (visitor.IsFullDbScan ? ReadFlags.HintReadAhead : ReadFlags.None);

    public void Accept(Hash256 rootHash)
    {
        if (!visitor.IsFullDbScan) visitor.VisitTree(default, rootHash);
        if (rootHash == Keccak.EmptyTreeHash)
        {
            if (visitor.IsFullDbScan) visitor.VisitTree(default, rootHash);
            return;
        }

        INodeRecordBuffer? buffer = bundle.FindRoot(null, out ulong pointer);
        if (!TryResolve(Reference(rootHash, TreePath.Empty), pointer, buffer, null, TreePath.Empty, default, out Resolved root)) return;

        if (visitor.IsFullDbScan) visitor.VisitTree(default, rootHash);
        Visit(root, default, null, TreePath.Empty, long.MaxValue);
    }

    private TrieNode Reference(Hash256 hash, in TreePath path)
    {
        RecordLookup(path);
        return new TrieNode(NodeType.Unknown, hash);
    }

    private void RecordLookup(in TreePath path)
    {
        diagnostics?.RecordLookup();
        diagnostics?.ObserveDepth(path.Length);
    }

    /// <summary>Decodes the node <paramref name="reference"/> names from the record at <paramref name="pointer"/>, otherwise from its RLP, embedded in the parent or loaded by path from persistence.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <returns>False, after visiting the node as missing, when persistence does not hold it either.</returns>
    private bool TryResolve(TrieNode reference, ulong pointer, INodeRecordBuffer? buffer, Hash256? address, in TreePath path,
        in TNodeContext context, out Resolved resolved)
    {
        TrieNode node = reference;
        NodeRecord record = default;
        if (buffer is not null && buffer.TryLocate(pointer, out record))
        {
            node = new TrieNode(NodeType.Unknown, reference.Keccak!, new CappedArray<byte>(record.Rlp.ToArray()));
        }
        else
        {
            buffer = null;
            if (reference.FullRlp.IsNull)
            {
                diagnostics?.RecordCacheMiss();
                byte[]? rlp = address is null
                    ? bundle.TryLoadStateRlp(path, reference.Keccak!, _readFlags)
                    : bundle.TryLoadStorageRlp(address, path, reference.Keccak!, _readFlags);
                if (rlp is null) return Missing(reference, context, out resolved);
                node = new TrieNode(NodeType.Unknown, reference.Keccak!, new CappedArray<byte>(rlp));
            }
        }

        try
        {
            node.ResolveNode(NullTrieNodeResolver.Instance, path);
        }
        catch (TrieException)
        {
            return Missing(reference, context, out resolved);
        }

        resolved = new Resolved(node, record, buffer);
        return true;
    }

    private bool Missing(TrieNode reference, in TNodeContext context, out Resolved resolved)
    {
        visitor.VisitMissingNode(context, reference.Keccak);
        resolved = default;
        return false;
    }

    private long Descend(TrieNode reference, ulong pointer, INodeRecordBuffer? buffer, in TNodeContext context, Hash256? address,
        in TreePath path, long sizeHint) =>
        TryResolve(reference, pointer, buffer, address, path, context, out Resolved node)
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
                    size += Descend(child, resolved.ChildPointer(0), resolved.Buffer, childContext, address, childPath, sizeHint);
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
        INodeRecordBuffer? buffer = bundle.FindRoot(address, out ulong pointer);
        return TryResolve(Reference(account.StorageRoot.ToCommitment(), TreePath.Empty), pointer, buffer, address, TreePath.Empty, leafContext, out Resolved storageRoot)
            ? Visit(storageRoot, leafContext.AddStorage(address), address, TreePath.Empty, sizeHint)
            : 0;
    }

    /// <returns>The child at <paramref name="index"/>, decoded when embedded in <paramref name="parent"/> and otherwise a reference to its hash; null when there is none.</returns>
    private TrieNode? Child(TrieNode parent, in TreePath path, int index, out TreePath childPath)
    {
        childPath = path;
        parent.AppendChildPath(ref childPath, index);
        TrieNode? child = parent.GetChildWithChildPath(NullTrieNodeResolver.Instance, ref childPath, index);
        if (child?.Keccak is not null) RecordLookup(childPath);
        return child;
    }

    private long VisitBranch(in Resolved resolved, in TNodeContext context, Hash256? address, TreePath path, long sizeHint)
    {
        sizeHint /= TrieNode.BranchesCount;
        // As in TrieNodeTraverser, fanning out only over the top state levels and the top storage level.
        bool parallel = options.MaxDegreeOfParallelism != 1 && path.Length <= (address is null ? 2 : 1);
        INodeRecordBuffer? buffer = resolved.Buffer;
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
                        return Descend(child, pointer, buffer, childContext, address, childPath, childSizeHint);
                    }));
                }
                else
                {
                    // Changing the size hint mid-iteration is deliberate, as in TrieNodeTraverser.
                    sizeHint = Descend(child, pointer, buffer, childContext, address, childPath, sizeHint);
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

    /// <summary>A decoded node, with the record it was decoded from while <see cref="Buffer"/> is set.</summary>
    private readonly record struct Resolved(TrieNode Node, NodeRecord Record, INodeRecordBuffer? Buffer)
    {
        /// <summary>The record pointer of the hashed child at <paramref name="index"/>; zero when unknown, which loads the child from persistence.</summary>
        public ulong ChildPointer(int index) => Buffer is null ? 0 : Record.View.Child(index).Pointer;
    }
}
