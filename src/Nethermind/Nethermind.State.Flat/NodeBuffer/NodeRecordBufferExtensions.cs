// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Operations on the shared node-record representation, independent of buffer storage policy.</summary>
public static class NodeRecordBufferExtensions
{
    private const int MaxPathNibbles = 64;

    /// <summary>The root of <paramref name="rootHash"/>, with its record pointer when the buffer indexes it.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    internal static (ValueHash256 Hash, ulong Pointer) ResolveRoot(this INodeRecordBuffer buffer, Hash256? address, in ValueHash256 rootHash) =>
        new(rootHash, rootHash == ValueKeccak.EmptyTreeHash ? 0 : buffer.FindRoot(address, rootHash, ulong.MaxValue));

    /// <summary>Appends a node's RLP with the given pointers to the records of its hashed children.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="hash">The keccak of <paramref name="rlp"/>, trusted as given.</param>
    /// <param name="childPointers">Child record pointers by branch nibble, or at index zero for an extension; zero or absent when unknown.</param>
    /// <exception cref="TrieException">The RLP is not a valid trie node.</exception>
    public static ulong AppendRecordFromRlp(this INodeRecordBuffer buffer, Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp, ReadOnlySpan<ulong> childPointers)
    {
        if (!NodeRecordCodec.TryDecode(rlp, out NodeRecordKind kind, out ushort hashMask)) ThrowInvalidRlp();

        int length = NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length);
        using INodeRecordWriteBatch batch = buffer.StartWriteBatch(NodeRecordCodec.AlignedLength(length));
        Span<byte> record = batch.Bytes;
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, hash, NodeRecordCodec.NodeKeyHash(address, path), record);
        int position = NodeRecordCodec.HeaderLength;
        for (int index = 0; index < childPointers.Length; index++)
        {
            if ((hashMask & (1 << index)) == 0) continue;
            BinaryPrimitives.WriteUInt64LittleEndian(record[position..], childPointers[index]);
            position += NodeRecordCodec.PointerLength;
        }
        return batch.BasePointer;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidRlp() => throw new TrieException("Invalid trie node RLP.");

    /// <summary>Copies the buffer-resident node at the exact path from the given root, or misses at an unavailable child.</summary>
    /// <remarks>Inline children have no separate record and are not returned.</remarks>
    internal static bool TryGetNode(this INodeRecordBuffer buffer, ulong rootPointer, in TreePath path, [NotNullWhen(true)] out TrieNode? node)
    {
        node = TryFindRecord(buffer, rootPointer, path, out NodeRecord record) ? record.ToTrieNode() : null;
        return node is not null;
    }

    /// <summary>Enumerates reachable buffer records in depth-first path order, stopping at unavailable child pointers.</summary>
    /// <remarks>
    /// Returned records are borrowed: the caller holds a pin protecting them for as long as it uses them.
    /// Inline children are contained in their parent's RLP and are not emitted as separate records.
    /// Pointers below <paramref name="lowerBound"/> and their subtrees are skipped: immutable buffer parents are written after their children.
    /// </remarks>
    public static IEnumerable<(TreePath Path, ulong Pointer, NodeRecord Record)> EnumerateNodes(this INodeRecordBuffer buffer, ulong rootPointer, ulong lowerBound = 0) => new NodeEnumerable(buffer, rootPointer, lowerBound);

    private static bool TryFindRecord(INodeRecordBuffer buffer, ulong rootPointer, in TreePath path, out NodeRecord record)
    {
        Span<byte> extensionKey = stackalloc byte[MaxPathNibbles];
        ulong pointer = rootPointer;
        TreePath reached = TreePath.Empty;
        while (pointer != 0 && buffer.TryLocate(pointer, out record))
        {
            if (reached.Length == path.Length) return true;

            NodeRecordRef view = record.View;
            ChildRef child;
            switch (view.Kind)
            {
                case NodeRecordKind.Extension:
                    int length = view.KeyLength(0);
                    if (length > path.Length - reached.Length) return Miss(out record);
                    view.CopyKey(0, extensionKey);
                    reached.AppendMut(extensionKey[..length]);
                    if (path.CompareToTruncated(reached, reached.Length) != 0) return Miss(out record);
                    child = view.Child(0);
                    break;
                case NodeRecordKind.HashedBranch:
                case NodeRecordKind.InlineBranch:
                    int nibble = path[reached.Length];
                    reached.AppendMut(nibble);
                    child = view.Child(nibble);
                    break;
                default:
                    return Miss(out record);
            }

            if (child.Kind != ChildRefKind.Hash) return Miss(out record);
            pointer = child.Pointer;
        }

        return Miss(out record);
    }

    private static bool Miss(out NodeRecord record)
    {
        record = default;
        return false;
    }

    private sealed class NodeEnumerable(INodeRecordBuffer buffer, ulong rootPointer, ulong lowerBound) : IEnumerable<(TreePath Path, ulong Pointer, NodeRecord Record)>
    {
        public IEnumerator<(TreePath Path, ulong Pointer, NodeRecord Record)> GetEnumerator() => new NodeEnumerator(buffer, rootPointer, lowerBound);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class NodeEnumerator(INodeRecordBuffer buffer, ulong rootPointer, ulong lowerBound) : IEnumerator<(TreePath Path, ulong Pointer, NodeRecord Record)>
    {
        private readonly INodeRecordBuffer _buffer = buffer;
        private readonly ulong _rootPointer = rootPointer;
        private readonly ulong _lowerBound = lowerBound;
        private readonly ArrayPoolList<Frame> _stack = new(4);
        private bool _started;
        private bool _disposed;

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
