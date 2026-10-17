// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

internal static partial class LogBulkSet
{
    internal delegate byte[]? LoadNodeRlp(in TreePath path, in ValueHash256 hash);

    /// <summary>Reads a pre-block path, then publishes newly fetched records from the deepest node to the root.</summary>
    /// <remarks>Stops silently where the path ends or persistence misses, retaining its verified prefix.</remarks>
    /// <param name="forDelete">Also loads the sibling a two-child branch collapses into when its matching leaf is deleted.</param>
    public static void WarmUp(TrieNodeLog log, WarmedNodes warmed, LoadNodeRlp loadRlp, Hash256? address,
        TrieRoot root, in ValueHash256 key, bool forDelete = false)
    {
        using TrieNodeLog.ReadLease lease = log.BeginRead();
        if (root.Hash == ValueKeccak.EmptyTreeHash) return;
        using ArrayPoolListRef<WarmUpFrame> frames = new(MaxKeyNibbles + 1);
        Span<byte> keyNibbles = stackalloc byte[MaxKeyNibbles];
        Span<byte> nodeKey = stackalloc byte[MaxKeyNibbles];
        Nibbles.BytesToNibbleBytes(key.Bytes, keyNibbles);
        TreePath path = TreePath.Empty;
        ChildRef child = ChildRef.ForHash(root.Hash, root.Pointer);
        int parentBranch = -1;
        while (child.Kind != ChildRefKind.Empty)
        {
            if (child.Kind == ChildRefKind.Inline)
            {
                Source inlineLeaf = Source.ForInline(child);
                int length = inlineLeaf.CopyKey(nodeKey);
                if (forDelete && nodeKey[..length].SequenceEqual(keyNibbles[path.Length..])) PrefetchWarmUpSibling(log, warmed, loadRlp, address, frames, parentBranch);
                break;
            }

            if (!TryReadWarmUpFrame(log, warmed, loadRlp, address, path, child, out WarmUpFrame frame)) break;
            frames.Add(frame);
            int current = frames.Count - 1;
            if (frame.Kind is NodeRecordKind.Leaf or NodeRecordKind.Extension)
            {
                int length = frame.CopyKey(nodeKey);
                if (frame.Kind == NodeRecordKind.Leaf)
                {
                    if (forDelete && nodeKey[..length].SequenceEqual(keyNibbles[path.Length..])) PrefetchWarmUpSibling(log, warmed, loadRlp, address, frames, parentBranch);
                    break;
                }
                if (!keyNibbles[path.Length..].StartsWith(nodeKey[..length])) break;

                frames.AsSpan()[current].ChildSlot = 0;
                path.AppendMut(nodeKey[..length]);
                child = frame.Child(0);
                parentBranch = -1;
            }
            else
            {
                if (path.Length == MaxKeyNibbles) break;
                int slot = keyNibbles[path.Length];
                frames.AsSpan()[current].ChildSlot = slot;
                path.AppendMut(slot);
                child = frame.Child(slot);
                parentBranch = current;
            }
        }

        Span<ulong> pointers = stackalloc ulong[BranchChildCount];
        ulong childPointer = 0;
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            ref WarmUpFrame frame = ref frames.AsSpan()[i];
            if (frame.RawRlp is null)
            {
                childPointer = frame.Pointer;
                continue;
            }

            pointers.Clear();
            if (frame.ChildSlot >= 0) pointers[frame.ChildSlot] = childPointer;
            if (frame.SiblingSlot >= 0) pointers[frame.SiblingSlot] = frame.SiblingPointer;
            warmed.TryInsertVerified(address, frame.Path, frame.Hash, frame.RawRlp, frame.Kind, frame.HashMask,
                pointers, out _, out childPointer);
        }
    }

    private static void PrefetchWarmUpSibling(TrieNodeLog log, WarmedNodes warmed, LoadNodeRlp loadRlp, Hash256? address,
        in ArrayPoolListRef<WarmUpFrame> frames, int branchIndex)
    {
        if (branchIndex < 0) return;
        ref WarmUpFrame branch = ref frames.AsSpan()[branchIndex];
        int siblingSlot = -1;
        ChildRef sibling = default;
        for (int slot = 0; slot < BranchChildCount; slot++)
        {
            if (slot == branch.ChildSlot) continue;
            ChildRef candidate = branch.Child(slot);
            if (candidate.Kind == ChildRefKind.Empty) continue;
            if (siblingSlot >= 0) return;
            siblingSlot = slot;
            sibling = candidate;
        }

        if (sibling.Kind != ChildRefKind.Hash || log.TryLocate(sibling.Pointer, out _)) return;
        TreePath siblingPath = branch.Path.Append(siblingSlot);
        if (!TryReadWarmUpFrame(log, warmed, loadRlp, address, siblingPath, sibling, out WarmUpFrame siblingFrame)) return;
        ulong pointer = siblingFrame.Pointer;
        if (siblingFrame.RawRlp is { } rlp)
        {
            warmed.TryInsertVerified(address, siblingPath, siblingFrame.Hash, rlp, siblingFrame.Kind, siblingFrame.HashMask,
                [], out _, out pointer);
        }
        branch.SiblingSlot = siblingSlot;
        branch.SiblingPointer = pointer;
    }

    private static bool TryReadWarmUpFrame(TrieNodeLog log, WarmedNodes warmed, LoadNodeRlp loadRlp, Hash256? address,
        in TreePath path, in ChildRef child, out WarmUpFrame frame)
    {
        ulong pointer = child.Pointer;
        if (log.TryLocate(pointer, out NodeRecordRef record) || warmed.TryGet(pointer, child.Data, out record)
            || warmed.TryGet(address, path, child.Data, out record, out pointer))
        {
            frame = new WarmUpFrame(path, child.Data, record.Kind, record.View.HashMask)
            {
                Record = record,
                Pointer = pointer,
            };
            return true;
        }

        byte[]? rlp = loadRlp(path, child.Data);
        if (rlp is not null && ValueKeccak.Compute(rlp) == child.Data && NodeRecord.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask))
        {
            frame = new WarmUpFrame(path, child.Data, kind, hashMask) { RawRlp = rlp };
            return true;
        }

        frame = default;
        return false;
    }

    private struct WarmUpFrame(in TreePath path, in ValueHash256 hash, NodeRecordKind kind, ushort hashMask)
    {
        public readonly TreePath Path = path;
        public readonly ValueHash256 Hash = hash;
        public readonly NodeRecordKind Kind = kind;
        public readonly ushort HashMask = hashMask;
        public NodeRecordRef Record;
        public byte[]? RawRlp;
        public ulong Pointer;
        public int ChildSlot = -1;
        public int SiblingSlot = -1;
        public ulong SiblingPointer;

        private readonly ReadOnlySpan<byte> Rlp => RawRlp is { } rlp ? rlp : Record.Rlp;

        public readonly int CopyKey(Span<byte> key)
        {
            NodeRlp.NextItem(Rlp, out NodeRlp.Item node, out _);
            NodeRlp.NextItem(node.Payload, out NodeRlp.Item prefix, out _);
            NodeRlp.ReadHexPrefix(prefix.Payload, 0, key);
            return NodeRlp.HexPrefixNibbleCount(prefix.Payload);
        }

        public readonly ChildRef Child(int slot)
        {
            if (RawRlp is null) return Record.View.Child(slot);
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
