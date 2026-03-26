// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Shared object pool for <see cref="RefCountingTrieNode"/> instances and their type-specialized impls.
/// Handles only object reuse — active-node tracking is done by <see cref="RefCountingRlpNodePoolTracker"/>.
/// </summary>
public sealed class RefCountingTrieNodePool
{
    private readonly ObjectPool<RefCountingTrieNode> _shellPool;
    private readonly ObjectPool<TrieNodeBranch> _branchPool;
    private readonly ObjectPool<TrieNodeExtension> _extensionPool;
    private readonly ObjectPool<TrieNodeLeaf> _leafPool;

    public RefCountingTrieNodePool(int maxPooled = 4096)
    {
        _shellPool = new DefaultObjectPool<RefCountingTrieNode>(new ShellPolicy(), maxPooled);
        _branchPool = new DefaultObjectPool<TrieNodeBranch>(new BranchPolicy(), maxPooled);
        _extensionPool = new DefaultObjectPool<TrieNodeExtension>(new ExtensionPolicy(), maxPooled);
        _leafPool = new DefaultObjectPool<TrieNodeLeaf>(new LeafPolicy(), maxPooled);
    }

    /// <summary>
    /// Rents a node from the pool, determines node type from RLP, initializes the correct impl,
    /// and returns a fully initialized <see cref="RefCountingTrieNode"/> bound to the given tracker.
    /// </summary>
    internal RefCountingTrieNode Rent(RefCountingRlpNodePoolTracker tracker, ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        RefCountingTrieNode shell = _shellPool.Get();
        shell.SetTracker(tracker);

        NodeType nodeType = DetermineNodeType(rlp);
        object impl = nodeType switch
        {
            NodeType.Branch => RentAndInitBranch(rlp),
            NodeType.Extension => RentAndInitExtension(rlp),
            _ => RentAndInitLeaf(rlp),
        };

        shell.Initialize(hash, nodeType, impl);
        return shell;
    }

    /// <summary>
    /// Returns a node and its impl to the appropriate pools.
    /// Called by <see cref="RefCountingRlpNodePoolTracker.Return"/>.
    /// </summary>
    internal void Return(RefCountingTrieNode node)
    {
        switch (node.NodeType)
        {
            case NodeType.Branch:
                TrieNodeBranch branch = Unsafe.As<TrieNodeBranch>(node.NodeImpl);
                branch.Clear();
                _branchPool.Return(branch);
                break;
            case NodeType.Extension:
                TrieNodeExtension ext = Unsafe.As<TrieNodeExtension>(node.NodeImpl);
                ext.Clear();
                _extensionPool.Return(ext);
                break;
            default:
                TrieNodeLeaf leaf = Unsafe.As<TrieNodeLeaf>(node.NodeImpl);
                leaf.Clear();
                _leafPool.Return(leaf);
                break;
        }

        _shellPool.Return(node);
    }

    private TrieNodeBranch RentAndInitBranch(ReadOnlySpan<byte> rlp)
    {
        TrieNodeBranch branch = _branchPool.Get();
        branch.Set(rlp);
        branch.ParseMetadata(rlp);
        return branch;
    }

    private TrieNodeExtension RentAndInitExtension(ReadOnlySpan<byte> rlp)
    {
        TrieNodeExtension ext = _extensionPool.Get();
        ext.Set(rlp);
        ext.ParseMetadata(rlp);
        return ext;
    }

    private TrieNodeLeaf RentAndInitLeaf(ReadOnlySpan<byte> rlp)
    {
        TrieNodeLeaf leaf = _leafPool.Get();
        leaf.Set(rlp);
        return leaf;
    }

    /// <summary>
    /// Determines the node type from RLP by counting sequence items and checking compact key prefix.
    /// Branch has 17 items; Extension/Leaf have 2 items distinguished by <c>(firstByte &amp; 0x20) != 0</c>.
    /// </summary>
    private static NodeType DetermineNodeType(ReadOnlySpan<byte> rlp)
    {
        if (rlp.IsEmpty) return NodeType.Leaf;

        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        int sequenceLength = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + sequenceLength;
        int itemCount = ctx.PeekNumberOfItemsRemaining(endPosition, maxSearch: 3);

        if (itemCount != 2) return NodeType.Branch;

        // Extension or Leaf — determined by the compact encoding prefix
        byte firstByte;
        if (rlp[ctx.Position] < 0x80)
        {
            firstByte = rlp[ctx.Position];
        }
        else
        {
            (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
            firstByte = rlp[ctx.Position];
        }

        return (firstByte & 0x20) != 0 ? NodeType.Leaf : NodeType.Extension;
    }

    private sealed class ShellPolicy : PooledObjectPolicy<RefCountingTrieNode>
    {
        public override RefCountingTrieNode Create() => new();
        public override bool Return(RefCountingTrieNode obj) => true;
    }

    private sealed class BranchPolicy : PooledObjectPolicy<TrieNodeBranch>
    {
        public override TrieNodeBranch Create() => new();
        public override bool Return(TrieNodeBranch obj) => true;
    }

    private sealed class ExtensionPolicy : PooledObjectPolicy<TrieNodeExtension>
    {
        public override TrieNodeExtension Create() => new();
        public override bool Return(TrieNodeExtension obj) => true;
    }

    private sealed class LeafPolicy : PooledObjectPolicy<TrieNodeLeaf>
    {
        public override TrieNodeLeaf Create() => new();
        public override bool Return(TrieNodeLeaf obj) => true;
    }
}
