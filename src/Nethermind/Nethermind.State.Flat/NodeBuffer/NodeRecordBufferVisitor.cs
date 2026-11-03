// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Numerics;
using Nethermind.Core;
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
    private static readonly ReadOnlySpanAction<byte, LoadTarget> Decode = static (rlp, target) => target.Record = DecodeRecord(rlp, target.Hash);

    /// <summary>Loads a trie's root, starting at the record block processing finds by hash in the buffer's root index.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <returns>False when neither the buffer nor persistence holds it.</returns>
    protected bool TryLoadRoot(Hash256? address, in ValueHash256 rootHash, out NodeRecord root)
    {
        RecordLookup(TreePath.Empty);
        return TryLoad(address, TreePath.Empty, rootHash, nodeRecordBuffer.ResolveRoot(address, rootHash).Pointer, out root);
    }

    /// <summary>Loads the node <paramref name="hash"/> names at <paramref name="path"/>: the buffer record at <paramref name="pointer"/>, otherwise the record of the RLP the persisted snapshots or persistence hold.</summary>
    /// <remarks>A record decoded from RLP has no child pointers, so every node below it is loaded from persistence too.</remarks>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <returns>False when neither holds a valid node.</returns>
    protected bool TryLoad(Hash256? address, in TreePath path, in ValueHash256 hash, ulong pointer, out NodeRecord record)
    {
        if (pointer != 0 && nodeRecordBuffer.TryLocate(pointer, out record)) return true;

        diagnostics?.RecordCacheMiss();
        NodeRecord? loaded = null;
        bundle.TryLoadPersistedRlp(address, path, readFlags, Decode, new LoadTarget(in hash, ref loaded));
        record = loaded.GetValueOrDefault();
        return loaded.HasValue;
    }

    /// <summary>The record of a child embedded in its parent's record.</summary>
    protected static NodeRecord InlineRecord(in ChildRef child) =>
        DecodeRecord(child.InlineRlp, default) ?? throw new TrieException("Invalid embedded trie node RLP.");

    /// <summary>Decodes the node a visitor callback takes from its record.</summary>
    /// <param name="inline">Whether the node is embedded in its parent, which leaves it without a hash.</param>
    protected static TrieNode ToTrieNode(NodeRecord record, bool inline, in TreePath path)
    {
        TrieNode node = inline ? new TrieNode(NodeType.Unknown, record.Rlp.ToArray()) : record.ToTrieNode();
        node.ResolveNode(NullTrieNodeResolver.Instance, path);
        return node;
    }

    protected void RecordLookup(in TreePath path)
    {
        diagnostics?.RecordLookup();
        diagnostics?.ObserveDepth(path.Length);
    }

    /// <returns>The record of <paramref name="rlp"/> with every child pointer 0, or null when it is not a valid trie node.</returns>
    private static NodeRecord? DecodeRecord(ReadOnlySpan<byte> rlp, in ValueHash256 hash)
    {
        if (!NodeRecordCodec.TryDecode(rlp, out NodeRecordKind kind, out ushort hashMask)) return null;

        byte[] record = new byte[NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length)];
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, hash, 0, record);
        return new NodeRecord(record, 0);
    }

    private readonly ref struct LoadTarget
    {
        public readonly ref readonly ValueHash256 Hash;
        public readonly ref NodeRecord? Record;

        public LoadTarget(ref readonly ValueHash256 hash, ref NodeRecord? record)
        {
            Hash = ref hash;
            Record = ref record;
        }
    }
}

/// <summary>Runs a tree visitor over the tries of a <see cref="ReadOnlySnapshotBundle"/> by following trie node buffer record pointers.</summary>
/// <remarks>
/// Calls the visitor as <see cref="PatriciaTree.Accept{TNodeContext}"/> does. Each trie starts at the root record block processing finds
/// by hash in the buffer's root index, and each child at the record its parent points at. A node no pointer resolves, and every node below
/// it, is loaded by path from the persisted snapshots and persistence instead. The walk reads only records: a <see cref="TrieNode"/> is
/// decoded just to hand a node to the visitor.
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

        if (!TryLoadRoot(null, rootHash, out NodeRecord root))
        {
            visitor.VisitMissingNode(default, rootHash);
            return;
        }

        if (visitor.IsFullDbScan) visitor.VisitTree(default, rootHash);
        Visit(root, inline: false, default, null, TreePath.Empty, long.MaxValue);
    }

    /// <summary>Whether the visitor descends into <paramref name="child"/>, counting a lookup of it when it is referenced by hash.</summary>
    private bool ShouldVisit(in ChildRef child, in TNodeContext childContext, in TreePath childPath)
    {
        if (child.Kind != ChildRefKind.Hash) return visitor.ShouldVisit(childContext, default);

        RecordLookup(childPath);
        return visitor.ShouldVisit(childContext, child.Data);
    }

    private long Descend(in ChildRef child, in TNodeContext context, Hash256? address, in TreePath path, long sizeHint)
    {
        if (child.Kind == ChildRefKind.Inline) return Visit(InlineRecord(child), inline: true, context, address, path, sizeHint);
        if (TryLoad(address, path, child.Data, child.Pointer, out NodeRecord record)) return Visit(record, inline: false, context, address, path, sizeHint);

        visitor.VisitMissingNode(context, child.Data);
        return 1;
    }

    private long Visit(NodeRecord record, bool inline, in TNodeContext context, Hash256? address, TreePath path, long sizeHint)
    {
        TrieNode node = ToTrieNode(record, inline, path);
        long size = 1;
        sizeHint--;
        switch (record.Kind)
        {
            case NodeRecordKind.HashedBranch:
            case NodeRecordKind.InlineBranch:
                visitor.VisitBranch(context, node);
                size += VisitBranch(record, context, address, path, sizeHint);
                break;
            case NodeRecordKind.Extension:
                visitor.VisitExtension(context, node);
                size += VisitExtension(record, context, address, path, sizeHint);
                break;
            case NodeRecordKind.Leaf:
                visitor.VisitLeaf(context, node);
                if (address is null && visitor.ExpectAccounts) size += VisitAccount(record, node, context, path, sizeHint);
                break;
            default:
                throw new TrieException($"An attempt was made to visit a {record.Kind} record");
        }
        return size;
    }

    private long VisitExtension(NodeRecord extension, in TNodeContext context, Hash256? address, TreePath path, long sizeHint)
    {
        NodeRecordRef view = extension.View;
        Span<byte> key = stackalloc byte[view.KeyLength(0)];
        view.CopyKey(0, key);
        path.AppendMut(key);
        TNodeContext childContext = context.Add(key);
        ChildRef child = view.Child(0);
        return ShouldVisit(child, childContext, path) ? Descend(child, childContext, address, path, sizeHint) : 0;
    }

    private long VisitAccount(NodeRecord leaf, TrieNode node, in TNodeContext context, TreePath path, long sizeHint)
    {
        RlpReader reader = new(leaf.LeafValue.Span);
        if (!AccountDecoder.Instance.TryDecodeStruct(ref reader, out AccountStruct account))
            throw new InvalidDataException("Non storage leaf should be an account");

        NodeRecordRef view = leaf.View;
        Span<byte> key = stackalloc byte[view.KeyLength(0)];
        view.CopyKey(0, key);
        TNodeContext leafContext = context.Add(key);
        visitor.VisitAccount(leafContext, node, account);
        if (!account.HasStorage || !visitor.ShouldVisit(leafContext, account.StorageRoot)) return 0;

        path.AppendMut(key);
        Hash256 address = path.Path.ToCommitment();
        if (TryLoadRoot(address, account.StorageRoot, out NodeRecord storageRoot))
            return Visit(storageRoot, inline: false, leafContext.AddStorage(address), address, TreePath.Empty, sizeHint);

        visitor.VisitMissingNode(leafContext, account.StorageRoot);
        return 0;
    }

    private long VisitBranch(NodeRecord branch, in TNodeContext context, Hash256? address, TreePath path, long sizeHint)
    {
        sizeHint /= TrieNode.BranchesCount;
        // As in TrieNodeTraverser, fanning out only over the top state levels and the top storage level.
        bool parallel = options.MaxDegreeOfParallelism != 1 && path.Length <= (address is null ? 2 : 1);
        Span<ChildRef> children = stackalloc ChildRef[TrieNode.BranchesCount];
        branch.View.CopyChildren(children);
        List<Task<long>>? tasks = null;
        long size = 0;
        try
        {
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                ChildRef child = children[i];
                if (child.Kind == ChildRefKind.Empty) continue;
                TreePath childPath = path.Append(i);
                TNodeContext childContext = context.Add((byte)i);
                if (!ShouldVisit(child, childContext, childPath)) continue;

                if (parallel && sizeHint > SubtreeSizeThreshold && _workers.TryTakeSlot(out ConcurrencyController.Slot slot))
                {
                    tasks ??= [];
                    long childSizeHint = sizeHint;
                    tasks.Add(Task.Run(() =>
                    {
                        using ConcurrencyController.Slot worker = slot;
                        return Descend(child, childContext, address, childPath, childSizeHint);
                    }));
                }
                else
                {
                    // Changing the size hint mid-iteration is deliberate, as in TrieNodeTraverser.
                    sizeHint = Descend(child, childContext, address, childPath, sizeHint);
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
