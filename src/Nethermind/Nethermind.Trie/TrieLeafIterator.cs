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

    public TrieLeafIterator(
        ITrieNodeResolver resolver,
        Hash256? rootHash,
        Action<TrieNodeException>? onException = null,
        in ValueHash256 startPath = default,
        in ValueHash256 endPath = default)
    {
        _resolver = resolver;
        _onException = onException;
        _stack = new StackFrame[MaxStackDepth];
        _startPath = startPath;
        _endPath = endPath;
        _hasRange = startPath != default || endPath != default;
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
                        if (_currentPath.Path.CompareTo(_startPath) < 0) continue; // Before start, skip

                        if (_currentPath.Path.CompareTo(_endPath) >= 0)
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
                        if (_hasRange && childPath.ToUpperBoundPath() < _startPath) break;
                        // Range check: stop if min path of subtree >= end
                        if (_hasRange && childPath.ToLowerBoundPath() >= _endPath)
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
                        if (_hasRange && childPath.ToLowerBoundPath() >= _endPath)
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
    /// Get the starting child index for a branch node based on the start path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetStartChildIndex(in TreePath branchPath, in ValueHash256 startPath)
    {
        if (branchPath.Path.CompareTo(startPath) >= 0) return 0;
        return new TreePath(startPath, FullPathLength)[branchPath.Length];
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
