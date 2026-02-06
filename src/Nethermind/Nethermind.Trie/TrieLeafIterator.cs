// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// Provides in-order (sorted by path) iteration over all leaf nodes in a Patricia trie.
/// Uses stack-based traversal to avoid recursion and yields leaves in lexicographical order.
/// </summary>
public ref struct TrieLeafIterator
{
    private const int MaxStackDepth = 128; // Generous depth for any practical trie
    private const int FullPathLength = 64; // 32 bytes = 64 nibbles

    private struct StackFrame
    {
        public TrieNode Node;
        public TreePath Path;
        public int ChildIndex; // For branch nodes: next child to visit (0-15)
        public bool Processed; // For leaf/extension: whether we've processed this node
    }

    private readonly ITrieNodeResolver _resolver;
    private readonly Action<TrieNodeException>? _onException;
    private readonly StackFrame[] _stack;
    private readonly ValueHash256 _startPath;
    private readonly ValueHash256 _endPath;
    private readonly bool _hasRange;
    private int _stackDepth;
    private TreePath _currentPath;
    private TrieNode? _currentLeaf;

    public TrieLeafIterator(ITrieNodeResolver resolver, Hash256? rootHash, Action<TrieNodeException>? onException = null)
    {
        _resolver = resolver;
        _onException = onException;
        _stack = new StackFrame[MaxStackDepth];
        _startPath = default;
        _endPath = default;
        _hasRange = false;
        _stackDepth = 0;
        _currentPath = default;
        _currentLeaf = null;

        if (rootHash is not null && rootHash != Keccak.EmptyTreeHash)
        {
            TreePath emptyPath = TreePath.Empty;
            TrieNode root = resolver.FindCachedOrUnknown(emptyPath, rootHash);
            Push(root, emptyPath);
        }
    }

    public TrieLeafIterator(ITrieNodeResolver resolver, Hash256? rootHash, in ValueHash256 startPath, in ValueHash256 endPath, Action<TrieNodeException>? onException = null)
    {
        _resolver = resolver;
        _onException = onException;
        _stack = new StackFrame[MaxStackDepth];
        _startPath = startPath;
        _endPath = endPath;
        _hasRange = true;
        _stackDepth = 0;
        _currentPath = default;
        _currentLeaf = null;

        if (rootHash is not null && rootHash != Keccak.EmptyTreeHash)
        {
            TreePath emptyPath = TreePath.Empty;
            TrieNode root = resolver.FindCachedOrUnknown(emptyPath, rootHash);
            Push(root, emptyPath);
        }
    }

    public readonly TreePath CurrentPath => _currentPath;
    public readonly TrieNode? CurrentLeaf => _currentLeaf;

    public bool MoveNext()
    {
        while (_stackDepth > 0)
        {
            ref StackFrame frame = ref _stack[_stackDepth - 1];

            // Resolve the node if needed
            try
            {
                frame.Node.ResolveNode(_resolver, frame.Path);
            }
            catch (TrieNodeException ex)
            {
                _onException?.Invoke(ex);
                Pop();
                continue;
            }

            switch (frame.Node.NodeType)
            {
                case NodeType.Leaf:
                    // Found a leaf - compute full path and check range
                    _currentPath = frame.Path.Append(frame.Node.Key);
                    _currentLeaf = frame.Node;
                    Pop();

                    // Check range bounds if applicable
                    if (_hasRange)
                    {
                        int cmpStart = ComparePath(_currentPath, _startPath);
                        if (cmpStart < 0) continue; // Before start, skip

                        int cmpEnd = ComparePath(_currentPath, _endPath);
                        if (cmpEnd >= 0)
                        {
                            // At or past end, stop iteration
                            _stackDepth = 0;
                            _currentLeaf = null;
                            _currentPath = default;
                            return false;
                        }
                    }
                    return true;

                case NodeType.Extension:
                    if (!frame.Processed)
                    {
                        frame.Processed = true;
                        // Follow the extension to its child
                        TreePath childPath = frame.Path.Append(frame.Node.Key);

                        // Range check for extension: skip if max path of subtree < start
                        if (_hasRange && IsSubtreeBeforeStart(childPath)) break;
                        // Range check: stop if min path of subtree >= end
                        if (_hasRange && IsSubtreeAtOrPastEnd(childPath))
                        {
                            _stackDepth = 0;
                            continue;
                        }

                        TrieNode? child = frame.Node.GetChildWithChildPath(_resolver, ref childPath, 0);
                        if (child is not null)
                        {
                            Push(child, childPath);
                        }
                    }
                    else
                    {
                        Pop();
                    }
                    break;

                case NodeType.Branch:
                    // Find next non-null child within range
                    bool foundChild = false;

                    // Compute start child index based on range (skip children before startPath)
                    if (_hasRange && frame.ChildIndex == 0)
                    {
                        frame.ChildIndex = GetStartChildIndex(frame.Path, _startPath);
                    }

                    while (frame.ChildIndex < 16)
                    {
                        int childIdx = frame.ChildIndex;
                        frame.ChildIndex++;

                        TreePath childPath = frame.Path.Append(childIdx);

                        // Range check: stop if child's min path >= end
                        if (_hasRange && IsSubtreeAtOrPastEnd(childPath))
                        {
                            _stackDepth = 0;
                            foundChild = true; // Exit the outer loop cleanly
                            break;
                        }

                        TrieNode? child = frame.Node.GetChildWithChildPath(_resolver, ref childPath, childIdx);
                        if (child is not null)
                        {
                            Push(child, childPath);
                            foundChild = true;
                            break;
                        }
                    }

                    if (!foundChild)
                    {
                        Pop();
                    }
                    break;

                default:
                    // Unknown or other node type - skip
                    Pop();
                    break;
            }
        }

        _currentLeaf = null;
        _currentPath = default;
        return false;
    }

    /// <summary>
    /// Compare a TreePath against a ValueHash256 path for range checking.
    /// Returns negative if path &lt; target, 0 if equal, positive if path &gt; target.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComparePath(in TreePath path, in ValueHash256 target)
    {
        ReadOnlySpan<byte> pathBytes = path.Path.Bytes;
        ReadOnlySpan<byte> targetBytes = target.Bytes;
        return pathBytes.SequenceCompareTo(targetBytes);
    }

    /// <summary>
    /// Check if the maximum possible path in a subtree is before startPath.
    /// Max path for a subtree at 'path' has all remaining nibbles as 0xF.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSubtreeBeforeStart(in TreePath path)
    {
        // Compare path prefix - if path prefix > start prefix, subtree is not before start
        int prefixNibbles = path.Length;
        int prefixBytes = prefixNibbles / 2;
        bool halfNibble = (prefixNibbles & 1) == 1;

        ReadOnlySpan<byte> pathPrefix = path.Path.Bytes[..prefixBytes];
        ReadOnlySpan<byte> startPrefix = _startPath.Bytes[..prefixBytes];

        int cmp = pathPrefix.SequenceCompareTo(startPrefix);
        if (cmp > 0) return false; // Path prefix greater, subtree overlaps or is after
        if (cmp < 0) return true;  // Path prefix less, max(subtree) could still be < start

        // Prefix bytes are equal, check the half nibble if present
        if (halfNibble)
        {
            int pathNibble = (path.Path.Bytes[prefixBytes] >> 4) & 0xF;
            int startNibble = (_startPath.Bytes[prefixBytes] >> 4) & 0xF;
            // Max path at this level has this nibble + 0xF in the rest
            // So if pathNibble < startNibble, max could still be < start
            if (pathNibble < startNibble) return true;
            if (pathNibble > startNibble) return false;
        }

        // At this point prefixes match exactly up to path.Length
        // The max of subtree has 0xFF... for remaining bytes, which is >= start
        return false;
    }

    /// <summary>
    /// Check if the minimum possible path in a subtree is at or past endPath.
    /// Min path for a subtree at 'path' has all remaining nibbles as 0x0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSubtreeAtOrPastEnd(in TreePath path)
    {
        // Compare path prefix against end prefix
        int prefixNibbles = path.Length;
        int prefixBytes = prefixNibbles / 2;
        bool halfNibble = (prefixNibbles & 1) == 1;

        ReadOnlySpan<byte> pathPrefix = path.Path.Bytes[..prefixBytes];
        ReadOnlySpan<byte> endPrefix = _endPath.Bytes[..prefixBytes];

        int cmp = pathPrefix.SequenceCompareTo(endPrefix);
        if (cmp > 0) return true;  // Path prefix greater, subtree is past end
        if (cmp < 0) return false; // Path prefix less, subtree min is before end

        // Prefix bytes are equal, check the half nibble if present
        if (halfNibble)
        {
            int pathNibble = (path.Path.Bytes[prefixBytes] >> 4) & 0xF;
            int endNibble = (_endPath.Bytes[prefixBytes] >> 4) & 0xF;
            if (pathNibble > endNibble) return true;
            if (pathNibble < endNibble) return false;
        }

        // At this point prefixes match exactly up to path.Length
        // The min of subtree has 0x00... for remaining bytes
        // This is < end (unless end is also all zeros from here, which would make it ==)
        // Check if end has any non-zero bytes remaining
        for (int i = halfNibble ? prefixBytes : prefixBytes; i < 32; i++)
        {
            byte endByte = _endPath.Bytes[i];
            byte mask = (i == prefixBytes && halfNibble) ? (byte)0x0F : (byte)0xFF;
            if ((endByte & mask) != 0) return false; // end has non-zero remainder, min < end
        }

        // end is exactly the path prefix with zeros, so min == end, which means >= end
        return true;
    }

    /// <summary>
    /// Get the starting child index for a branch node based on the start path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetStartChildIndex(in TreePath branchPath, in ValueHash256 startPath)
    {
        int nibbleIndex = branchPath.Length;
        if (nibbleIndex >= FullPathLength) return 0;

        // Compare prefix - if branch path > start, start from 0
        int byteIndex = nibbleIndex / 2;
        bool isHighNibble = (nibbleIndex & 1) == 0;

        // Compare the path up to this point
        int prefixBytes = byteIndex;
        ReadOnlySpan<byte> pathPrefix = branchPath.Path.Bytes[..prefixBytes];
        ReadOnlySpan<byte> startPrefix = startPath.Bytes[..prefixBytes];
        int cmp = pathPrefix.SequenceCompareTo(startPrefix);
        if (cmp > 0) return 0;
        if (cmp < 0) return 16; // Branch is before start entirely - but this shouldn't happen in normal traversal

        // Prefixes match, extract the nibble
        byte startByte = startPath.Bytes[byteIndex];
        int startNibble = isHighNibble ? (startByte >> 4) & 0xF : startByte & 0xF;

        return startNibble;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(TrieNode node, in TreePath path)
    {
        if (_stackDepth >= MaxStackDepth)
        {
            ThrowStackOverflow();
        }

        ref StackFrame frame = ref _stack[_stackDepth];
        frame.Node = node;
        frame.Path = path;
        frame.ChildIndex = 0;
        frame.Processed = false;
        _stackDepth++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Pop()
    {
        _stackDepth--;
    }

    private static void ThrowStackOverflow() => throw new InvalidOperationException("TrieLeafIterator stack overflow - trie depth exceeds maximum");
}
