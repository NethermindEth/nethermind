// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using BulkSetEntry = Nethermind.Trie.PatriciaTree.BulkSetEntry;

namespace Nethermind.Trie.NodeBuffer;

internal static partial class BufferBulkSet
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
        public readonly NodeRecord Record;
        public readonly byte Skip;
        public readonly ValueHash256 Inline;
        public readonly byte InlineLength;

        private Source(SourceKind kind, NodeRecord record, int skip, in ValueHash256 inline, byte inlineLength)
        {
            Kind = kind;
            Record = record;
            Skip = (byte)skip;
            Inline = inline;
            InlineLength = inlineLength;
        }

        public static Source ForRecord(NodeRecord record, int skip) => new(SourceKind.Record, record, skip, default, 0);

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
                NodeRecordRef view = Record.View;
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

    [InlineArray(BranchChildCount)]
    private struct BranchArray<T>
    {
        private T _element0;
    }

    /// <summary>A branch level whose sixteen children are updated in parallel, each by a task that commits the child's subtree in an arena of its own, so only committed references cross threads.</summary>
    private sealed class ParallelChildren
    {
        public bool Flipped;
        public TreePath Path;
        public int RemainderNibble;
        public Source Remainder;

        /// <summary>Each child's entries in the entries' array, or in the sort buffer's array when <see cref="Flipped"/>.</summary>
        public BranchArray<(int Start, int Count)> Ranges;

        /// <summary>The references to the children before the join, and the committed references of those in <see cref="ChangedMask"/> after it.</summary>
        public BranchArray<ChildRef> Children;

        public int ChangedMask;
    }

    private readonly partial struct Context
    {
        /// <summary>Applies <paramref name="entries"/>, whose keys all start with <paramref name="path"/>, to <paramref name="node"/> at it.</summary>
        /// <param name="sortBuffer">Scratch of the same length, which may swap roles with <paramref name="entries"/> below this level.</param>
        /// <param name="flipped">Whether <paramref name="entries"/> is in the sort buffer's array rather than the entries' array.</param>
        public Updated Update(Span<BulkSetEntry> entries, Span<BulkSetEntry> sortBuffer, bool flipped, ref TreePath path, in Source node)
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
                flipped = !flipped;
            }

            bool remainderKept = remainderNibble >= 0;
            bool changed = false;
            bool hasRemove = false;
            int nonEmptyCount = 0;
            ParallelChildren? parallelChildren = IsParallel(entries.Length, mask) ? new ParallelChildren() : null;
            if (parallelChildren is not null) UpdateInParallel(parallelChildren, entries, flipped, path, indexes, node, childrenStart, remainderNibble, remainder);
            path.AppendMut(0);
            for (int remaining = mask; remaining != 0;)
            {
                int nibble = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                int start = indexes[nibble];
                int end = remaining == 0 ? entries.Length : indexes[BitOperations.TrailingZeroCount(remaining)];
                path.SetLast(nibble);

                bool isRemainder = nibble == remainderNibble;
                ChildRef reference;
                if (parallelChildren is not null)
                {
                    if ((parallelChildren.ChangedMask & (1 << nibble)) == 0) continue;

                    reference = parallelChildren.Children[nibble];
                }
                else
                {
                    Source child = isRemainder ? remainder
                        : Load(path, childrenStart >= 0 ? _arena.Children[childrenStart + nibble] : node.Record.View.Child(nibble));
                    Updated updated = Update(entries[start..end], sortBuffer[start..end], flipped, ref path, child);
                    if (!updated.Changed) continue;

                    reference = updated.Reference;
                }

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

            int pendingIndex = (hasRemove || isNew) && nonEmptyCount < 2
                ? MaybeCombine(childrenStart, ref path)
                : _arena.AddBranch(childrenStart);
            return Updated.To(pendingIndex);
        }

        private bool IsParallel(int count, int mask) =>
            _canParallelize && mask == FullMask && count >= PatriciaTree.MinEntriesToParallelizeThreshold && !RuntimeInformation.IsSingleProcessor;

        /// <summary>Updates the sixteen children of <paramref name="node"/> at <paramref name="path"/> in parallel into <paramref name="level"/>, as the serial loop of <see cref="Update"/> would.</summary>
        /// <remarks>An unchanged remainder is left to the caller, which keeps it pending below the new branch.</remarks>
        private void UpdateInParallel(ParallelChildren level, Span<BulkSetEntry> entries, bool flipped, in TreePath path, ReadOnlySpan<int> indexes,
            in Source node, int childrenStart, int remainderNibble, in Source remainder)
        {
            int offset = PatriciaTree.GetSpanOffset(flipped ? _sortBuffer : _entries, entries);
            level.Flipped = flipped;
            level.Path = path;
            level.RemainderNibble = remainderNibble;
            level.Remainder = remainder;
            for (int nibble = 0; nibble < BranchChildCount; nibble++)
            {
                int end = nibble == BranchChildCount - 1 ? entries.Length : indexes[nibble + 1];
                level.Ranges[nibble] = (offset + indexes[nibble], end - indexes[nibble]);
                if (nibble != remainderNibble) level.Children[nibble] = childrenStart >= 0 ? _arena.Children[childrenStart + nibble] : node.Record.View.Child(nibble);
            }

            ParallelUnbalancedWork.For(0, BranchChildCount, ParallelUnbalancedWork.DefaultOptions, (Context: this, Level: level),
                static (nibble, state) =>
                {
                    state.Context.UpdateChild(state.Level, nibble);
                    return state;
                });
        }

        /// <summary>Updates child <paramref name="nibble"/> of <paramref name="level"/> in an arena of its own, committing it when it changed.</summary>
        private void UpdateChild(ParallelChildren level, int nibble)
        {
            BulkSetArena arena = BulkSetArena.Rent();
            try
            {
                Context context = new(_store, _address, arena, _entries, _sortBuffer, _canParallelize);
                (int start, int count) = level.Ranges[nibble];
                Span<BulkSetEntry> entries = (level.Flipped ? _sortBuffer : _entries).AsSpan(start, count);
                Span<BulkSetEntry> sortBuffer = (level.Flipped ? _entries : _sortBuffer).AsSpan(start, count);
                TreePath path = level.Path.Append(nibble);
                Source child = nibble == level.RemainderNibble ? level.Remainder : context.Load(path, level.Children[nibble]);
                Updated updated = context.Update(entries, sortBuffer, level.Flipped, ref path, child);
                if (!updated.Changed) return;

                level.Children[nibble] = updated.PendingIndex < 0 ? default : context.Commit(updated.PendingIndex, path, isRoot: false);
                Interlocked.Or(ref level.ChangedMask, 1 << nibble);
            }
            finally
            {
                ReturnArena(arena);
            }
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
            // A branch keeps its path, so its reference with the buffer pointer moves under the extension; anything else is re-encoded shorter.
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
        private int CopyChildren(NodeRecord branch)
        {
            int childrenStart = _arena.AddChildren();
            NodeRecordRef view = branch.View;
            for (int i = 0; i < BranchChildCount; i++)
            {
                _arena.Children[childrenStart + i] = view.Child(i);
            }

            return childrenStart;
        }

        /// <summary>The root node, read by its supplied pointer like any child.</summary>
        /// <exception cref="MissingTrieNodeException">The store has no such node.</exception>
        public Source LoadRoot(in TrieRoot root) =>
            root.Hash == ValueKeccak.EmptyTreeHash ? default : Load(TreePath.Empty, ChildRef.ForHash(root.Hash, root.Pointer));

        /// <summary>The node <paramref name="child"/> at <paramref name="path"/> refers to, read in place from the buffer when its pointer resolves, otherwise loaded by the store.</summary>
        /// <exception cref="MissingTrieNodeException">The store has no such node.</exception>
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
            if (_store.TryLocate(child.Pointer, out NodeRecord record))
            {
                _arena.NodesFromBuffer++;
                return Source.ForRecord(record, 0);
            }

            if (_store.TryLoad(path, child.Data, out record))
            {
                _arena.NodesFromSource++;
                return Source.ForRecord(record, 0);
            }

            return ThrowMissingNode(_address, path, child.Data);
        }
    }
}
