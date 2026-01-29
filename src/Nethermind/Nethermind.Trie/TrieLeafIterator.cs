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

    private struct StackFrame
    {
        public TrieNode Node;
        public TreePath Path;
        public int ChildIndex; // For branch nodes: next child to visit (0-15)
        public bool Processed; // For leaf/extension: whether we've processed this node
    }

    private readonly ITrieNodeResolver _resolver;
    private readonly StackFrame[] _stack;
    private int _stackDepth;
    private TreePath _currentPath;
    private TrieNode? _currentLeaf;

    public TrieLeafIterator(ITrieNodeResolver resolver, Hash256? rootHash)
    {
        _resolver = resolver;
        _stack = new StackFrame[MaxStackDepth];
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
            catch (TrieNodeException)
            {
                Pop();
                continue;
            }

            switch (frame.Node.NodeType)
            {
                case NodeType.Leaf:
                    // Found a leaf - return it
                    _currentPath = frame.Path.Append(frame.Node.Key);
                    _currentLeaf = frame.Node;
                    Pop();
                    return true;

                case NodeType.Extension:
                    if (!frame.Processed)
                    {
                        frame.Processed = true;
                        // Follow the extension to its child
                        TreePath childPath = frame.Path.Append(frame.Node.Key);
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
                    // Find next non-null child
                    bool foundChild = false;
                    while (frame.ChildIndex < 16)
                    {
                        int childIdx = frame.ChildIndex;
                        frame.ChildIndex++;

                        if (!frame.Node.IsChildNull(childIdx))
                        {
                            TreePath childPath = frame.Path.Append(childIdx);
                            TrieNode? child = frame.Node.GetChildWithChildPath(_resolver, ref childPath, childIdx);
                            if (child is not null)
                            {
                                Push(child, childPath);
                                foundChild = true;
                                break;
                            }
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
