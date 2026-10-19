// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Collections;

namespace Nethermind.Trie.NodeBuffer;

public sealed partial class NodeRecordBuffer
{
    private const int MaxPathNibbles = 64;

    /// <summary>Copies the buffer-resident node at the exact path from the given root, or misses at an unavailable child.</summary>
    /// <remarks>Inline children have no separate record and are not returned.</remarks>
    internal bool TryGetNode(ulong rootPointer, in TreePath path, [NotNullWhen(true)] out TrieNode? node)
    {
        using ReadLease lease = BeginRead();
        node = TryFindRecord(rootPointer, path, out _, out NodeRecord record) ? record.ToTrieNode() : null;
        return node is not null;
    }

    /// <summary>Finds the buffer pointer of the node at the exact path from the given root.</summary>
    /// <remarks>
    /// The returned pointer does not retain its record. A caller looking it up later must hold an operation lease
    /// spanning lookup and use, or a generation retain protecting it. Inline children have no separate pointer.
    /// </remarks>
    internal bool TryGetNodePointer(ulong rootPointer, in TreePath path, out ulong pointer)
    {
        using ReadLease lease = BeginRead();
        return TryFindRecord(rootPointer, path, out pointer, out _);
    }

    /// <summary>Enumerates reachable buffer records in depth-first path order, stopping at unavailable child pointers.</summary>
    /// <remarks>
    /// Each enumerator holds a read lease through completion or disposal. It protects records and lookup metadata retired
    /// during enumeration, but cannot restore pointers already unavailable when it started. Returned records borrow this
    /// operation's memory; buffering records or pointers past enumeration requires an outer read lease.
    /// Inline children are contained in their parent's RLP and are not emitted as separate records.
    /// Pointers below <paramref name="lowerBound"/> and their subtrees are skipped: immutable buffer parents are written after their children.
    /// </remarks>
    internal IEnumerable<(TreePath Path, ulong Pointer, NodeRecord Record)> EnumerateNodes(ulong rootPointer, ulong lowerBound = 0) => new NodeEnumerable(this, rootPointer, lowerBound);

    private bool TryFindRecord(ulong rootPointer, in TreePath path, out ulong pointer, out NodeRecord record)
    {
        Span<byte> extensionKey = stackalloc byte[MaxPathNibbles];
        pointer = rootPointer;
        int depth = 0;
        while (pointer != 0 && TryLocate(pointer, out record))
        {
            if (depth == path.Length) return true;

            NodeRecordRef view = record.View;
            ChildRef child;
            switch (view.Kind)
            {
                case NodeRecordKind.Extension:
                    int length = view.KeyLength(0);
                    if (length > path.Length - depth) return Miss(out pointer, out record);
                    view.CopyKey(0, extensionKey);
                    for (int i = 0; i < length; i++)
                    {
                        if (extensionKey[i] != path[depth + i]) return Miss(out pointer, out record);
                    }
                    depth += length;
                    child = view.Child(0);
                    break;
                case NodeRecordKind.HashedBranch:
                case NodeRecordKind.InlineBranch:
                    child = view.Child(path[depth++]);
                    break;
                default:
                    return Miss(out pointer, out record);
            }

            if (child.Kind != ChildRefKind.Hash) return Miss(out pointer, out record);
            pointer = child.Pointer;
        }

        return Miss(out pointer, out record);
    }

    private static bool Miss(out ulong pointer, out NodeRecord record)
    {
        pointer = 0;
        record = default;
        return false;
    }

    private sealed class NodeEnumerable(NodeRecordBuffer buffer, ulong rootPointer, ulong lowerBound) : IEnumerable<(TreePath Path, ulong Pointer, NodeRecord Record)>
    {
        public IEnumerator<(TreePath Path, ulong Pointer, NodeRecord Record)> GetEnumerator() => new NodeEnumerator(buffer, rootPointer, lowerBound);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class NodeEnumerator : IEnumerator<(TreePath Path, ulong Pointer, NodeRecord Record)>
    {
        private readonly NodeRecordBuffer _buffer;
        private readonly ulong _rootPointer;
        private readonly ulong _lowerBound;
        private readonly ArrayPoolList<Frame> _stack;
        private bool _started;
        private bool _disposed;

        public NodeEnumerator(NodeRecordBuffer buffer, ulong rootPointer, ulong lowerBound)
        {
            _buffer = buffer;
            _rootPointer = rootPointer;
            _lowerBound = lowerBound;
            _stack = new ArrayPoolList<Frame>(4);
            try
            {
                // Unlike an iterator method, this enumerator can own a read operation across MoveNext calls.
                buffer.EnterRead();
            }
            catch
            {
                _stack.Dispose();
                throw;
            }
        }

        public (TreePath Path, ulong Pointer, NodeRecord Record) Current { get; private set; }

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_disposed) return false;
            try
            {
                return MoveNextCore();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private bool MoveNextCore()
        {
            if (!_started)
            {
                _started = true;
                if (_rootPointer != 0 && _rootPointer >= _lowerBound && _buffer.TryLocate(_rootPointer, out NodeRecord root))
                {
                    return Visit(TreePath.Empty, _rootPointer, root);
                }
            }

            Span<byte> extensionKey = stackalloc byte[MaxPathNibbles];
            while (_stack.Count > 0)
            {
                ref Frame frame = ref _stack.GetRef(_stack.Count - 1);
                if (frame.RemainingChildren == 0)
                {
                    _stack.RemoveAt(_stack.Count - 1);
                    continue;
                }

                int nibble = BitOperations.TrailingZeroCount(frame.RemainingChildren);
                frame.RemainingChildren &= frame.RemainingChildren - 1;
                // Hash-child pointers are packed in nibble order; traversal needs no child RLP or hash decoding.
                ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(frame.Record.Span[(NodeRecordCodec.HeaderLength + frame.NextPointer++ * NodeRecordCodec.PointerLength)..]);
                if (pointer == 0 || pointer < _lowerBound) continue;

                TreePath path = frame.Path;
                if (frame.Record.Kind == NodeRecordKind.Extension)
                {
                    NodeRecordRef view = frame.Record.View;
                    int length = view.KeyLength(0);
                    view.CopyKey(0, extensionKey);
                    path.AppendMut(extensionKey[..length]);
                }
                else path.AppendMut(nibble);

                if (_buffer.TryLocate(pointer, out NodeRecord record))
                {
                    return Visit(path, pointer, record);
                }
            }

            Dispose();
            return false;
        }

        private bool Visit(in TreePath path, ulong pointer, NodeRecord record)
        {
            Current = (path, pointer, record);
            if (record.Kind is NodeRecordKind.Extension or NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch)
            {
                _stack.Add(new Frame { Path = path, Record = record, RemainingChildren = record.View.HashMask });
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stack.Dispose();
            Current = default;
            _buffer.EndRead();
        }

        public void Reset() => throw new NotSupportedException();

        private struct Frame
        {
            public TreePath Path;
            public NodeRecord Record;
            public uint RemainingChildren;
            public int NextPointer;
        }
    }
}
