// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.NodeBuffer;

internal sealed class NodeRecordBufferVisitor<TNodeContext> where TNodeContext : struct, INodeContext<TNodeContext>
{
    private const long SubtreeSizeThreshold = 256 * 1024;
    private readonly INodeRecordBuffer _buffer;
    private readonly ITreeVisitor<TNodeContext> _visitor;
    private readonly VisitingStats? _diagnostics;
    private readonly int _parallelism;
    private readonly ConcurrencyController _workers;
    private readonly ReadFlags _readFlags;

    internal NodeRecordBufferVisitor(INodeRecordBuffer buffer, ITreeVisitor<TNodeContext> visitor, VisitingOptions? options = null, VisitingStats? diagnostics = null)
    {
        _buffer = buffer;
        _visitor = visitor;
        _diagnostics = diagnostics;
        _parallelism = (options ?? VisitingOptions.Default).MaxDegreeOfParallelism;
        _workers = new ConcurrencyController(_parallelism);
        _readFlags = visitor.ExtraReadFlag | (visitor.IsFullDbScan ? ReadFlags.HintReadAhead : ReadFlags.None);
    }

    internal void Accept(Hash256 rootHash, ITrieNodeResolver persistence)
    {
        if (!_visitor.IsFullDbScan) _visitor.VisitTree(default, rootHash);
        if (rootHash == Keccak.EmptyTreeHash)
        {
            if (_visitor.IsFullDbScan) _visitor.VisitTree(default, rootHash);
            return;
        }

        TreePath path = TreePath.Empty;
        TrieNode root = Reference(rootHash, path);
        ulong pointer = _buffer.FindRoot(null, rootHash);
        if (!TryLoad(ref root, pointer, persistence, path, default, out NodeRecord record)) return;
        if (_visitor.IsFullDbScan) _visitor.VisitTree(default, rootHash);
        Visit(root, record, default, persistence, path, false, long.MaxValue);
    }

    private TrieNode Reference(Hash256 hash, in TreePath path)
    {
        RecordLookup(path);
        return new TrieNode(NodeType.Unknown, hash);
    }

    private void RecordLookup(in TreePath path)
    {
        _diagnostics?.RecordLookup();
        _diagnostics?.ObserveDepth(path.Length);
    }

    private bool TryLoad(ref TrieNode node, ulong pointer, ITrieNodeResolver persistence, in TreePath path,
        in TNodeContext context, out NodeRecord record)
    {
        record = default;
        try
        {
            if (_buffer.TryLocate(pointer, out record))
            {
                node = new TrieNode(NodeType.Unknown, node.Keccak!, record.Rlp);
            }
            else if (node.NodeType == NodeType.Unknown && node.FullRlp.IsNull)
            {
                _diagnostics?.RecordCacheMiss();
                byte[] rlp = persistence.LoadRlp(path, node.Keccak!, _readFlags)
                    ?? throw new TrieException($"Trie returned a NULL RLP for node {node.Keccak}");
                node = new TrieNode(NodeType.Unknown, node.Keccak!, new CappedArray<byte>(rlp));
            }
            node.ResolveNode(NullTrieNodeResolver.Instance, path);
        }
        catch (TrieException)
        {
            _visitor.VisitMissingNode(context, node.Keccak);
            return false;
        }
        return true;
    }

    private long Descend(TrieNode node, ulong pointer, in TNodeContext context, ITrieNodeResolver persistence,
        TreePath path, bool storage, long sizeHint) =>
        TryLoad(ref node, pointer, persistence, path, context, out NodeRecord record)
            ? Visit(node, record, context, persistence, path, storage, sizeHint)
            : 1;

    private long Visit(TrieNode node, NodeRecord record, in TNodeContext context, ITrieNodeResolver persistence,
        TreePath path, bool storage, long sizeHint)
    {
        long size = 1;
        sizeHint--;
        switch (node.NodeType)
        {
            case NodeType.Branch:
                _visitor.VisitBranch(context, node);
                size += VisitBranch(node, record, context, persistence, path, storage, sizeHint);
                break;
            case NodeType.Extension:
                _visitor.VisitExtension(context, node);
                TreePath childPath = path;
                childPath.AppendMut(node.Key!);
                TrieNode child = Child(node, record, path, childPath, 0, out ulong pointer)
                    ?? throw new InvalidDataException($"Child of an extension {node.Key} should not be null.");
                TNodeContext childContext = context.Add(node.Key!);
                if (_visitor.ShouldVisit(childContext, child.Keccak!))
                    size += Descend(child, pointer, childContext, persistence, childPath, storage, sizeHint);
                break;
            case NodeType.Leaf:
                _visitor.VisitLeaf(context, node);
                if (!storage && _visitor.ExpectAccounts)
                {
                    RlpReader decoder = new(node.Value.AsSpan());
                    if (!AccountDecoder.Instance.TryDecodeStruct(ref decoder, out AccountStruct account))
                        throw new InvalidDataException("Non storage leaf should be an account");
                    TNodeContext leafContext = context.Add(node.Key!);
                    _visitor.VisitAccount(leafContext, node, account);
                    if (account.HasStorage && _visitor.ShouldVisit(leafContext, account.StorageRoot))
                    {
                        path.AppendMut(node.Key!);
                        Hash256 address = path.Path.ToCommitment();
                        TreePath storagePath = TreePath.Empty;
                        TrieNode storageRoot = Reference(account.StorageRoot.ToCommitment(), storagePath);
                        size += Descend(storageRoot, _buffer.FindRoot(address, account.StorageRoot), leafContext.AddStorage(address),
                            persistence.GetStorageTrieNodeResolver(address), storagePath, true, sizeHint);
                    }
                }
                break;
            default:
                throw new TrieException($"An attempt was made to visit a node {node.Keccak} of type {node.NodeType}");
        }
        return size;
    }

    private TrieNode? Child(TrieNode parent, NodeRecord record, TreePath parentPath, in TreePath childPath, int index, out ulong pointer)
    {
        pointer = 0;
        if (!record.IsNull)
        {
            ChildRef child = record.View.Child(index);
            switch (child.Kind)
            {
                case ChildRefKind.Empty:
                    return null;
                case ChildRefKind.Hash:
                    pointer = child.Pointer;
                    return Reference(child.Data.ToCommitment(), childPath);
                case ChildRefKind.Inline:
                    return new TrieNode(NodeType.Unknown, child.InlineRlp.ToArray());
                default:
                    throw new TrieException("Unexpected pending child in a buffer record");
            }
        }

        TrieNode? node = parent.GetChild(NullTrieNodeResolver.Instance, ref parentPath, index);
        if (node?.Keccak is not null) RecordLookup(childPath);
        return node;
    }

    private long VisitBranch(TrieNode node, NodeRecord record, in TNodeContext context, ITrieNodeResolver persistence,
        TreePath path, bool storage, long sizeHint)
    {
        sizeHint /= TrieNode.BranchesCount;
        bool parallel = _parallelism != 1 && (storage ? path.Length <= 1 : path.Length <= 2);
        List<Task<long>>? tasks = null;
        long size = 0;
        try
        {
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TreePath childPath = path;
                childPath.AppendMut((byte)i);
                TrieNode? child = Child(node, record, path, childPath, i, out ulong pointer);
                if (child is null) continue;
                TNodeContext childContext = context.Add((byte)i);
                if (!_visitor.ShouldVisit(childContext, child.Keccak!)) continue;
                if (parallel && sizeHint > SubtreeSizeThreshold && _workers.TryTakeSlot(out ConcurrencyController.Slot slot))
                {
                    tasks ??= [];
                    long childSizeHint = sizeHint;
                    tasks.Add(Task.Run(() =>
                    {
                        using ConcurrencyController.Slot worker = slot;
                        return Descend(child, pointer, childContext, persistence, childPath, storage, childSizeHint);
                    }));
                }
                else
                {
                    sizeHint = Descend(child, pointer, childContext, persistence, childPath, storage, sizeHint);
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
