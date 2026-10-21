// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Loads the nodes on a trie path into a <see cref="WarmedNodeBuffer"/> ahead of a <see cref="NodeRecordBulkSet"/> that changes it.</summary>
internal static class BufferWarmUp
{
    private const int MaxKeyNibbles = BulkSetWorkingState.MaxKeyNibbles;
    private const int BranchChildCount = BulkSetWorkingState.BranchChildCount;
    // Fits a full-depth path of branches with sixteen hashed children, about 46 KiB.
    private const int RecordsLength = 64 * 1024;

    private static readonly ReadOnlySpanAction<byte, ArrayPoolList<byte>> CopyRlp = static (rlp, scratch) => scratch.AddRange(rlp);

    /// <summary>Reads a pre-block path, then publishes newly fetched records from the deepest node to the root.</summary>
    /// <remarks>
    /// Descends while reading and publishes only once the path is read, so every published record links the records published
    /// before it. Stops silently where the path ends or <paramref name="reader"/> misses, retaining its verified prefix.
    /// </remarks>
    /// <param name="buffer">The buffer holding the pre-block trie, which also resolves the pointers of <paramref name="warmed"/>.</param>
    /// <param name="reader">Reads the nodes neither <paramref name="buffer"/> nor <paramref name="warmed"/> holds.</param>
    /// <param name="forDelete">Also loads the sibling a two-child branch collapses into when its matching leaf is deleted.</param>
    public static void WarmUp(INodeRecordBuffer buffer, WarmedNodeBuffer warmed, ReadOnlySnapshotBundle reader, Hash256? address,
        (ValueHash256 Hash, ulong Pointer) root, in ValueHash256 key, bool forDelete)
    {
        if (root.Hash == ValueKeccak.EmptyTreeHash) return;
        TreePath keyPath = new(key, MaxKeyNibbles);
        Span<byte> nodeKey = stackalloc byte[MaxKeyNibbles];
        using ArrayPoolList<Frame> frames = new(MaxKeyNibbles + 1);
        using ArrayPoolList<byte> scratch = new(1024);
        using ArrayPoolList<byte> records = new(SafeArrayPool<byte>.Shared, RecordsLength, RecordsLength, clearFirst: false);
        int recordsLength = 0;

        TreePath path = TreePath.Empty;
        ChildRef child = ChildRef.ForHash(root.Hash, root.Pointer);
        // Whether the path ends at a leaf of the key, as only its deletion collapses the branch above it.
        bool reachedKey = false;
        while (child.Kind != ChildRefKind.Empty)
        {
            if (child.Kind == ChildRefKind.Inline)
            {
                // Only leaves are embedded in their parent.
                int length = Key(child.InlineRlp, nodeKey);
                reachedKey = path.Length + length == MaxKeyNibbles && path.Append(nodeKey[..length]) == keyPath;
                break;
            }

            if (!TryRead(buffer, warmed, reader, address, path, child, scratch, records, ref recordsLength, out Frame frame)) break;

            ReadOnlySpan<byte> rlp = frame.Record.Rlp;
            TreePath childPath = path;
            if (frame.Record.Kind is NodeRecordKind.Leaf or NodeRecordKind.Extension)
            {
                int length = Key(rlp, nodeKey);
                if (frame.Record.Kind == NodeRecordKind.Leaf) reachedKey = path.Length + length == MaxKeyNibbles && path.Append(nodeKey[..length]) == keyPath;
                else if (length <= MaxKeyNibbles - path.Length)
                {
                    childPath.AppendMut(nodeKey[..length]);
                    if (keyPath.StartsWith(childPath)) frame.ChildSlot = 0;
                }
            }
            else if (path.Length < MaxKeyNibbles)
            {
                frame.ChildSlot = keyPath[path.Length];
                childPath.AppendMut(frame.ChildSlot);
            }

            frames.Add(frame);
            if (frame.ChildSlot < 0) break;
            child = frame.Record.View.Child(frame.ChildSlot);
            path = childPath;
        }

        // The parent of the matching leaf: the last frame for an embedded leaf, otherwise the one before it.
        int branchIndex = frames.Count > 0 && frames[^1].Record.Kind == NodeRecordKind.Leaf ? frames.Count - 2 : frames.Count - 1;
        if (forDelete && reachedKey && branchIndex >= 0) PrefetchSibling(buffer, warmed, reader, address, ref frames.GetRef(branchIndex), scratch);

        Span<ulong> pointers = stackalloc ulong[BranchChildCount];
        ulong childPointer = 0;
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            ref Frame frame = ref frames.GetRef(i);
            if (frame.Pointer != 0)
            {
                childPointer = frame.Pointer;
                continue;
            }

            pointers.Clear();
            if (frame.ChildSlot >= 0) pointers[frame.ChildSlot] = childPointer;
            if (frame.SiblingSlot >= 0) pointers[frame.SiblingSlot] = frame.SiblingPointer;
            warmed.Append(address, frame.Path, frame.Record.Hash, frame.Record.Rlp, pointers, out _, out childPointer);
        }
    }

    /// <summary>Reads the node <paramref name="child"/> refers to in place, or writes its record from <paramref name="reader"/> to <paramref name="records"/>.</summary>
    /// <remarks>Read RLP is trusted to be the node of the hash its parent holds: the pre-block path reaches the reader only below a subtree unchanged since persistence.</remarks>
    /// <param name="recordsLength">The bytes of <paramref name="records"/> in use, advanced past a written record.</param>
    /// <returns>False when the reader misses, the RLP is not a valid trie node, or its record does not fit <paramref name="records"/>.</returns>
    private static bool TryRead(INodeRecordBuffer buffer, WarmedNodeBuffer warmed, ReadOnlySnapshotBundle reader, Hash256? address,
        in TreePath path, in ChildRef child, ArrayPoolList<byte> scratch, ArrayPoolList<byte> records, ref int recordsLength, out Frame frame)
    {
        frame = new Frame { Path = path, ChildSlot = -1, SiblingSlot = -1 };
        ulong pointer = child.Pointer;
        if (buffer.TryLocate(pointer, out NodeRecord record) || warmed.TryGet(address, path, out record, out pointer))
        {
            frame.Record = record;
            frame.Pointer = pointer;
            return true;
        }

        scratch.Clear();
        if (!reader.TryLoadPersistedRlp(address, path, ReadFlags.None, CopyRlp, scratch)
            || !NodeRecordCodec.TryDecode(scratch.AsSpan(), out NodeRecordKind kind, out ushort hashMask)) return false;

        int length = NodeRecordCodec.Length(BitOperations.PopCount(hashMask), scratch.Count);
        if (length > records.Count - recordsLength) return false;
        NodeRecordCodec.WriteFromRlp(scratch.AsSpan(), kind, hashMask, child.Data, 0, records.AsSpan()[recordsLength..]);
        frame.Record = new NodeRecord(records.AsReadOnlyMemory(), recordsLength);
        recordsLength += length;
        return true;
    }

    /// <summary>Loads the only other child of <paramref name="branch"/>, which it collapses into when the matching leaf is deleted.</summary>
    private static void PrefetchSibling(INodeRecordBuffer buffer, WarmedNodeBuffer warmed, ReadOnlySnapshotBundle reader, Hash256? address,
        ref Frame branch, ArrayPoolList<byte> scratch)
    {
        if (branch.Record.Kind is not (NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch)) return;

        int siblingSlot = -1;
        ChildRef sibling = default;
        for (int slot = 0; slot < BranchChildCount; slot++)
        {
            if (slot == branch.ChildSlot) continue;
            ChildRef candidate = branch.Record.View.Child(slot);
            if (candidate.Kind == ChildRefKind.Empty) continue;
            if (siblingSlot >= 0) return;
            siblingSlot = slot;
            sibling = candidate;
        }

        if (sibling.Kind != ChildRefKind.Hash || buffer.TryLocate(sibling.Pointer, out _)) return;

        TreePath siblingPath = branch.Path.Append(siblingSlot);
        if (!warmed.TryGet(address, siblingPath, out _, out ulong pointer))
        {
            scratch.Clear();
            if (!reader.TryLoadPersistedRlp(address, siblingPath, ReadFlags.None, CopyRlp, scratch)) return;
            warmed.Append(address, siblingPath, sibling.Data, scratch.AsSpan(), [], out _, out pointer);
        }

        branch.SiblingSlot = siblingSlot;
        branch.SiblingPointer = pointer;
    }

    /// <summary>Copies the key nibbles of the leaf or extension <paramref name="rlp"/> to <paramref name="key"/>.</summary>
    /// <returns>The number of nibbles copied.</returns>
    private static int Key(ReadOnlySpan<byte> rlp, Span<byte> key)
    {
        RlpReader reader = new(rlp);
        reader.ReadSequenceLength();
        ReadOnlySpan<byte> prefix = reader.DecodeByteArraySpan();
        NodeRlp.ReadHexPrefix(prefix, 0, key);
        return NodeRlp.HexPrefixNibbleCount(prefix);
    }

    /// <summary>A node on the warmed path: a record read in place, or one written to the scratch records, to be published.</summary>
    private struct Frame
    {
        public TreePath Path;
        public NodeRecord Record;
        // The pointer of a record read in place, or 0 for a scratch record.
        public ulong Pointer;
        // The slot of the child on the path, or -1 where the path ends.
        public int ChildSlot;
        // The slot and pointer of the sibling a delete collapses into, or -1 when none is loaded.
        public int SiblingSlot;
        public ulong SiblingPointer;
    }
}
