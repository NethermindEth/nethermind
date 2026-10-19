// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Loads the nodes on a trie path into a <see cref="WarmedNodeLog"/> ahead of a <see cref="NodeRecordBulkSet"/> that changes it.</summary>
internal static class BufferWarmUp
{
    private const int MaxKeyNibbles = BulkSetArena.MaxKeyNibbles;
    private const int BranchChildCount = BulkSetArena.BranchChildCount;

    internal delegate void LoadNodeRlp(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, WarmUpRead> action, WarmUpRead read);

    /// <summary>Reads a pre-block path, then publishes newly fetched records from the deepest node to the root.</summary>
    /// <remarks>Stops silently where the path ends or persistence misses, retaining its verified prefix. Descent stays inside each read callback so ancestors' borrowed RLP remains valid until their children are published.</remarks>
    /// <param name="buffer">The buffer holding the pre-block trie, which also resolves the pointers of <paramref name="warmed"/>.</param>
    /// <param name="forDelete">Also loads the sibling a two-child branch collapses into when its matching leaf is deleted.</param>
    public static void WarmUp(INodeRecordBuffer buffer, WarmedNodeLog warmed, LoadNodeRlp loadRlp, Hash256? address,
        TrieRoot root, in ValueHash256 key, bool forDelete = false)
    {
        if (root.Hash == ValueKeccak.EmptyTreeHash) return;
        Span<byte> keyNibbles = stackalloc byte[MaxKeyNibbles];
        Nibbles.BytesToNibbleBytes(key.Bytes, keyNibbles);
        WarmUpRead.Start(buffer, warmed, loadRlp, address, root, keyNibbles, forDelete);
    }

    private readonly ref struct WarmUpSibling(in WarmUpFrame branch, in TreePath path, int childSlot, Span<ulong> pointers)
    {
        public readonly WarmUpFrame Branch = branch;
        public readonly TreePath Path = path;
        public readonly int ChildSlot = childSlot;
        public readonly Span<ulong> Pointers = pointers;
    }

    internal readonly ref struct WarmUpRead
    {
        private static readonly ReadOnlySpanAction<byte, WarmUpRead> Read = static (rlp, read) => read.ReadRlp(rlp);
        private readonly INodeRecordBuffer _buffer;
        private readonly WarmedNodeLog _warmed;
        private readonly LoadNodeRlp _loadRlp;
        private readonly Hash256? _address;
        private readonly TreePath _path;
        private readonly ValueHash256 _hash;
        private readonly ReadOnlySpan<byte> _keyNibbles;
        private readonly bool _forDelete;
        private readonly ref ulong _result;
        private readonly WarmUpSibling _sibling;

        internal static void Start(INodeRecordBuffer buffer, WarmedNodeLog warmed, LoadNodeRlp loadRlp, Hash256? address,
            TrieRoot root, ReadOnlySpan<byte> keyNibbles, bool forDelete)
        {
            ulong result = 0;
            WarmUpRead read = new(buffer, warmed, loadRlp, address, TreePath.Empty, root.Hash, keyNibbles, forDelete, ref result);
            read.Load(ChildRef.ForHash(root.Hash, root.Pointer));
        }

        private WarmUpRead(INodeRecordBuffer buffer, WarmedNodeLog warmed, LoadNodeRlp loadRlp, Hash256? address,
            in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> keyNibbles, bool forDelete, ref ulong result, WarmUpSibling sibling = default)
        {
            _buffer = buffer;
            _warmed = warmed;
            _loadRlp = loadRlp;
            _address = address;
            _path = path;
            _hash = hash;
            _keyNibbles = keyNibbles;
            _forDelete = forDelete;
            _result = ref result;
            _sibling = sibling;
        }

        private void Load(in ChildRef child)
        {
            if (child.Kind == ChildRefKind.Empty) return;
            if (child.Kind == ChildRefKind.Inline)
            {
                Span<byte> nodeKey = stackalloc byte[MaxKeyNibbles];
                int length = new WarmUpFrame(child.InlineRlp, NodeRecordKind.Leaf, 0, default, 0).CopyKey(nodeKey);
                WarmDeleteSibling(nodeKey[..length]);
                return;
            }

            ulong pointer = child.Pointer;
            bool found = _buffer.TryLocate(pointer, out NodeRecord record);
            if (found)
            {
                if (NodeRecordCodec.IsTransient(pointer)) Metrics.IncrementWarmedNodeCacheHits();
            }
            else
            {
                found = _warmed.TryGet(_address, _path, child.Data, out record, out pointer);
                if (found) Metrics.IncrementWarmedNodeCacheHits();
                else Metrics.IncrementWarmedNodeCacheMisses();
            }

            if (found) Walk(new WarmUpFrame(record.Rlp, record.Kind, record.View.HashMask, record, pointer));
            else _loadRlp(_path, child.Data, Read, this);
        }

        private void ReadRlp(ReadOnlySpan<byte> rlp)
        {
            if (ValueKeccak.Compute(rlp) == _hash && NodeRecordCodec.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask))
                Walk(new WarmUpFrame(rlp, kind, hashMask, default, 0));
        }

        private void Walk(in WarmUpFrame frame)
        {
            Span<byte> nodeKey = stackalloc byte[MaxKeyNibbles];
            int childSlot = -1;
            TreePath childPath = _path;
            if (frame.Kind is NodeRecordKind.Leaf or NodeRecordKind.Extension)
            {
                int length = frame.CopyKey(nodeKey);
                if (frame.Kind == NodeRecordKind.Leaf)
                    WarmDeleteSibling(nodeKey[..length]);
                else if (_keyNibbles[_path.Length..].StartsWith(nodeKey[..length]))
                {
                    childSlot = 0;
                    childPath.AppendMut(nodeKey[..length]);
                }
            }
            else if (_path.Length < MaxKeyNibbles)
            {
                childSlot = _keyNibbles[_path.Length];
                childPath.AppendMut(childSlot);
            }

            Span<ulong> pointers = stackalloc ulong[BranchChildCount];
            pointers.Clear();
            if (childSlot >= 0)
            {
                ChildRef child = frame.Child(childSlot);
                ulong childResult = 0;
                WarmUpSibling sibling = frame.Kind is NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch
                    ? new WarmUpSibling(frame, _path, childSlot, pointers) : default;
                WarmUpRead read = new(_buffer, _warmed, _loadRlp, _address, childPath, child.Data, _keyNibbles, _forDelete, ref childResult, sibling);
                read.Load(child);
                pointers[childSlot] = childResult;
            }

            if (frame.Record.IsNull)
                _warmed.AppendVerified(_address, _path, _hash, frame.Rlp, frame.Kind, frame.HashMask, pointers, out _, out _result);
            else _result = frame.Pointer;
        }

        private void WarmDeleteSibling(ReadOnlySpan<byte> nodeKey)
        {
            if (_forDelete && !_sibling.Pointers.IsEmpty && nodeKey.SequenceEqual(_keyNibbles[_path.Length..]))
                PrefetchSibling(_sibling.Branch, _sibling.Path, _sibling.ChildSlot, _sibling.Pointers);
        }

        private void PrefetchSibling(in WarmUpFrame branch, in TreePath branchPath, int childSlot, Span<ulong> pointers)
        {
            int siblingSlot = -1;
            ChildRef sibling = default;
            for (int slot = 0; slot < BranchChildCount; slot++)
            {
                if (slot == childSlot) continue;
                ChildRef candidate = branch.Child(slot);
                if (candidate.Kind == ChildRefKind.Empty) continue;
                if (siblingSlot >= 0) return;
                siblingSlot = slot;
                sibling = candidate;
            }

            if (sibling.Kind != ChildRefKind.Hash || _buffer.TryLocate(sibling.Pointer, out _)) return;
            TreePath siblingPath = branchPath.Append(siblingSlot);
            ulong result = 0;
            WarmUpRead read = new(_buffer, _warmed, _loadRlp, _address, siblingPath, sibling.Data, [], false, ref result);
            read.LoadSibling(sibling);
            pointers[siblingSlot] = result;
        }

        private void LoadSibling(in ChildRef child)
        {
            if (_warmed.TryGet(_address, _path, child.Data, out _, out ulong pointer))
            {
                Metrics.IncrementWarmedNodeCacheHits();
                _result = pointer;
            }
            else
            {
                Metrics.IncrementWarmedNodeCacheMisses();
                _loadRlp(_path, child.Data, static (rlp, read) =>
                {
                    read._warmed.TryAppend(read._address, read._path, read._hash, rlp, out _, out read._result);
                }, this);
            }
        }
    }

    private readonly ref struct WarmUpFrame(ReadOnlySpan<byte> rlp, NodeRecordKind kind, ushort hashMask, NodeRecord record, ulong pointer)
    {
        public readonly ReadOnlySpan<byte> Rlp = rlp;
        public readonly NodeRecordKind Kind = kind;
        public readonly ushort HashMask = hashMask;
        public readonly NodeRecord Record = record;
        public readonly ulong Pointer = pointer;

        public int CopyKey(Span<byte> key)
        {
            NodeRlp.NextItem(Rlp, out NodeRlp.Item node, out _);
            NodeRlp.NextItem(node.Payload, out NodeRlp.Item prefix, out _);
            NodeRlp.ReadHexPrefix(prefix.Payload, 0, key);
            return NodeRlp.HexPrefixNibbleCount(prefix.Payload);
        }

        public ChildRef Child(int slot)
        {
            if (!Record.IsNull) return Record.View.Child(slot);
            NodeRlp.NextItem(Rlp, out NodeRlp.Item node, out _);
            ReadOnlySpan<byte> remaining = node.Payload;
            int skip = Kind == NodeRecordKind.Extension ? 1 : slot;
            for (int i = 0; i < skip; i++) NodeRlp.NextItem(remaining, out _, out remaining);
            NodeRlp.NextItem(remaining, out NodeRlp.Item item, out _);
            return item.IsList ? ChildRef.ForInline(item.Encoded)
                : item.Payload.IsEmpty ? default : ChildRef.ForHash(new ValueHash256(item.Payload), 0);
        }
    }
}
