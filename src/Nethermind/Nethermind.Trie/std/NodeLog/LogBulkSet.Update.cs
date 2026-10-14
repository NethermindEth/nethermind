// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Crypto;
using BulkSetEntry = Nethermind.Trie.PatriciaTree.BulkSetEntry;

namespace Nethermind.Trie.NodeLog;

internal static partial class LogBulkSet
{
    private enum SourceKind : byte
    {
        None,
        Record,
        InlineLeaf,
    }

    /// <summary>A node the walk reads in place: a record or a leaf embedded in its parent, less the first <see cref="Skip"/> key nibbles that the fake branches above it dropped.</summary>
    /// <remarks>Only a leaf or an extension is ever skipped.</remarks>
    private readonly struct Source
    {
        public readonly SourceKind Kind;
        public readonly NodeRecordRef Record;
        public readonly byte Skip;
        public readonly ValueHash256 Inline;
        public readonly byte InlineLength;

        private Source(SourceKind kind, NodeRecordRef record, int skip, in ValueHash256 inline, byte inlineLength)
        {
            Kind = kind;
            Record = record;
            Skip = (byte)skip;
            Inline = inline;
            InlineLength = inlineLength;
        }

        public static Source ForRecord(NodeRecordRef record, int skip) => new(SourceKind.Record, record, skip, default, 0);

        public static Source ForInline(in ChildRef child) => new(SourceKind.InlineLeaf, default, 0, child.Data, child.InlineLength);

        public Source SkipOne() => new(Kind, Record, Skip + 1, Inline, InlineLength);

        public NodeRecordKind NodeKind => Kind == SourceKind.Record ? Record.Kind : NodeRecordKind.Leaf;

        public bool IsBranch => NodeKind is NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch;

        /// <summary>Copies the key nibbles past <see cref="Skip"/>; a branch has none.</summary>
        /// <returns>The number of nibbles copied.</returns>
        public int CopyKey(Span<byte> nibbles)
        {
            if (Kind == SourceKind.Record)
            {
                RecordView view = Record.View;
                view.CopyKey(Skip, nibbles);
                return view.KeyLength(Skip);
            }

            ReadOnlySpan<byte> hexPrefix = ReadInlineLeaf(out _);
            NodeRlp.ReadHexPrefix(hexPrefix, Skip, nibbles);
            return NodeRlp.HexPrefixNibbleCount(hexPrefix) - Skip;
        }

        /// <remarks>An embedded leaf's value is copied, as it lives in this struct.</remarks>
        public ValueRef LeafValue()
        {
            if (Kind == SourceKind.Record) return Record.LeafValue;

            ReadInlineLeaf(out ReadOnlySpan<byte> value);
            return new ValueRef(value.ToArray(), 0, value.Length);
        }

        private ReadOnlySpan<byte> ReadInlineLeaf(out ReadOnlySpan<byte> value)
        {
            NodeRlp.NextItem(Inline.Bytes[..InlineLength], out NodeRlp.Item node, out _);
            NodeRlp.NextItem(node.Payload, out NodeRlp.Item key, out ReadOnlySpan<byte> rest);
            NodeRlp.NextItem(rest, out NodeRlp.Item item, out _);
            value = item.Payload;
            return key.Payload;
        }
    }

    /// <summary>What applying entries to a node did: nothing, handing <see cref="Original"/> back, or a change to the pending node at <see cref="PendingIndex"/>, -1 when removed.</summary>
    private readonly struct Updated(bool changed, in Source original, int pendingIndex)
    {
        public readonly bool Changed = changed;
        public readonly Source Original = original;
        public readonly int PendingIndex = pendingIndex;

        public static Updated Unchanged(in Source original) => new(false, original, -1);

        public static Updated To(int pendingIndex) => new(true, default, pendingIndex);

        public ChildRef Reference => PendingIndex < 0 ? default : ChildRef.ForPending(PendingIndex);
    }

    private readonly partial struct Context
    {
        /// <summary>Applies <paramref name="entries"/>, whose keys all start with <paramref name="path"/>, to <paramref name="node"/> at it.</summary>
        /// <param name="sortBuffer">Scratch of the same length, which may swap roles with <paramref name="entries"/> below this level.</param>
        public Updated Update(Span<BulkSetEntry> entries, Span<BulkSetEntry> sortBuffer, ref TreePath path, in Source node)
        {
            switch (entries.Length)
            {
                case 0:
                    return Updated.Unchanged(node);
                case 1:
                    return SetOne(entries[0], ref path, node);
            }

            int depth = path.Length;
            if (depth == MaxKeyNibbles) ThrowNonUniqueKeys();

            bool isNew = !node.IsBranch;
            int remainderNibble = -1;
            Source remainder = default;
            // A branch record's children are copied out only once one changes.
            int childrenStart = node.Kind == SourceKind.None ? _arena.AddChildren()
                : isNew ? MakeFakeBranch(node, out remainderNibble, out remainder)
                : -1;

            Span<int> indexes = stackalloc int[BranchChildCount];
            int mask;
            if (entries.Length <= TinySortMaxCount)
            {
                mask = PatriciaTree.SortTiny(entries, depth, indexes);
            }
            else if (entries.Length < InPlaceSortThreshold)
            {
                mask = PatriciaTree.InPlaceBucketSort16(entries, depth, indexes);
            }
            else
            {
                mask = PatriciaTree.BucketSort16Large(entries, sortBuffer, depth, indexes);
                Span<BulkSetEntry> sorted = sortBuffer;
                sortBuffer = entries;
                entries = sorted;
            }

            bool remainderKept = remainderNibble >= 0;
            bool changed = false;
            bool hasRemove = false;
            int nonEmptyCount = 0;
            path.AppendMut(0);
            for (int remaining = mask; remaining != 0;)
            {
                int nibble = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                int start = indexes[nibble];
                int end = remaining == 0 ? entries.Length : indexes[BitOperations.TrailingZeroCount(remaining)];
                path.SetLast(nibble);

                bool isRemainder = nibble == remainderNibble;
                Source child = isRemainder ? remainder
                    : Load(path, childrenStart >= 0 ? _arena.Children[childrenStart + nibble] : node.Record.View.Child(nibble));
                Updated updated = Update(entries[start..end], sortBuffer[start..end], ref path, child);
                if (!updated.Changed) continue;

                ChildRef reference = updated.Reference;
                changed = true;
                if (isRemainder) remainderKept = false;
                if (reference.Kind == ChildRefKind.Empty) hasRemove = true;
                else nonEmptyCount++;

                if (childrenStart < 0) childrenStart = CopyChildren(node.Record);
                _arena.Children[childrenStart + nibble] = reference;
            }

            path.TruncateOne();

            // Unlike PatriciaTree.MaybeCombineNode, there is no node to compare with the original: a fake branch whose children
            // all stay is the original, and anything else changes some key under it.
            if (!changed) return Updated.Unchanged(node);

            // A remainder no entry changed stays below the new branch, with the key the branch shortened.
            if (remainderKept) _arena.Children[childrenStart + remainderNibble] = ChildRef.ForPending(IntoNode(remainder));

            return Updated.To((hasRemove || isNew) && nonEmptyCount < 2
                ? MaybeCombine(childrenStart, ref path)
                : _arena.AddBranch(childrenStart));
        }

        /// <summary>Turns the branch at <paramref name="path"/> left with fewer than two children into nothing, an extension, or its only child with the nibble prepended.</summary>
        /// <returns>The pending node replacing the branch, or -1 when it is removed.</returns>
        private int MaybeCombine(int childrenStart, ref TreePath path)
        {
            int onlyChild = -1;
            for (int i = 0; i < BranchChildCount; i++)
            {
                if (_arena.Children[childrenStart + i].Kind == ChildRefKind.Empty) continue;
                if (onlyChild >= 0) return _arena.AddBranch(childrenStart);

                onlyChild = i;
            }

            if (onlyChild < 0) return -1;

            ReadOnlySpan<byte> prefix = [(byte)onlyChild];
            ChildRef child = _arena.Children[childrenStart + onlyChild];
            if (child.Kind == ChildRefKind.Pending) return Prepend(prefix, child.PendingIndex);

            path.AppendMut(onlyChild);
            Source source = Load(path, child);
            path.TruncateOne();
            // A branch keeps its path, so its reference with the log pointer moves under the extension; anything else is re-encoded shorter.
            return source.IsBranch ? _arena.AddExtension(prefix, child) : Prepend(prefix, IntoNode(source));
        }

        /// <summary>Splits a leaf or an extension into the children of a new branch: its child when its key is a single nibble, otherwise what remains of it below its first nibble.</summary>
        /// <returns>The index of the branch's children.</returns>
        private int MakeFakeBranch(in Source source, out int remainderNibble, out Source remainder)
        {
            int childrenStart = _arena.AddChildren();
            Span<byte> key = stackalloc byte[MaxKeyNibbles];
            int keyLength = source.CopyKey(key);
            if (source.NodeKind == NodeRecordKind.Extension && keyLength == 1)
            {
                ChildRef child = source.Record.View.Child(0);
                _arena.Children[childrenStart + key[0]] = child;
                remainderNibble = -1;
                remainder = default;
                return childrenStart;
            }

            remainderNibble = key[0];
            remainder = source.SkipOne();
            return childrenStart;
        }

        /// <summary>Copies a leaf or an extension out into a pending node, to be changed or kept below a new node.</summary>
        private int IntoNode(in Source source)
        {
            Debug.Assert(!source.IsBranch, "Only leaves and extensions are copied out.");
            Span<byte> key = stackalloc byte[MaxKeyNibbles];
            int keyLength = source.CopyKey(key);
            return source.NodeKind == NodeRecordKind.Extension
                ? _arena.AddExtension(key[..keyLength], source.Record.View.Child(0))
                : _arena.AddLeaf(key[..keyLength], source.LeafValue());
        }

        /// <returns>The index of a copy of the branch's sixteen children.</returns>
        private int CopyChildren(NodeRecordRef branch)
        {
            int childrenStart = _arena.AddChildren();
            RecordView view = branch.View;
            for (int i = 0; i < BranchChildCount; i++)
            {
                _arena.Children[childrenStart + i] = view.Child(i);
            }

            return childrenStart;
        }

        /// <summary>The root node, read in place from the log by its pointer while that resolves, otherwise like <see cref="Load"/> with the pointer the log indexes for it.</summary>
        /// <exception cref="MissingTrieNodeException">The node is in neither the log nor the source.</exception>
        public Source LoadRoot(in TrieRoot root)
        {
            if (root.Hash == ValueKeccak.EmptyTreeHash) return default;

            if (_log.TryLocate(root.Pointer, out NodeRecordRef record))
            {
                _arena.NodesFromLog++;
                return Source.ForRecord(record, 0);
            }

            return Load(TreePath.Empty, ChildRef.ForHash(root.Hash, _log.FindRoot(_address, root.Hash)));
        }

        /// <summary>The node <paramref name="child"/> at <paramref name="path"/> refers to, read in place from the log when its pointer resolves, otherwise from the source.</summary>
        /// <exception cref="MissingTrieNodeException">The node is in neither.</exception>
        public Source Load(in TreePath path, in ChildRef child)
        {
            switch (child.Kind)
            {
                case ChildRefKind.Empty:
                    return default;
                case ChildRefKind.Inline:
                    return Source.ForInline(child);
            }

            Debug.Assert(child.Kind == ChildRefKind.Hash, "Only children of nodes read are loaded.");
            if (_log.TryLocate(child.Pointer, out NodeRecordRef record))
            {
                _arena.NodesFromLog++;
                return Source.ForRecord(record, 0);
            }

            if (_source.TryLoad(path, child.Data, out record))
            {
                _arena.NodesFromSource++;
                return Source.ForRecord(record, 0);
            }

            return ThrowMissingNode(_address, path, child.Data);
        }
    }
}
