// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Serialization.Rlp;

using BulkSetEntry = Nethermind.Trie.PatriciaTree.BulkSetEntry;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Applies entries to a trie of node records, writing every new node to the trie node buffer, like <see cref="PatriciaTree.BulkSet"/>.</summary>
/// <remarks>
/// A node referenced by hash is read in place from the store's buffer when its pointer resolves, and otherwise loaded by the store, without being
/// added to the buffer. Entries are bucketed by nibble one level at a time; a leaf or extension the entries split is walked in place, less
/// the key nibbles the split dropped, and copied out only when it changes. New nodes stay pending in a <see cref="BulkSetWorkingState"/> until
/// the commit encodes them straight into the buffer one path depth at a time, deepest first, hashing each level together.
/// A full branch level with enough entries may update its children in parallel, each task committing its subtree in its own working state.
/// Nodes that become unreachable are not removed from the buffer.
/// </remarks>
internal static class NodeRecordBulkSet
{
    private const int InPlaceSortThreshold = 32;
    private const int TinySortMaxCount = 3;
    private const int MaxKeyNibbles = BulkSetWorkingState.MaxKeyNibbles;
    private const int BranchChildCount = BulkSetWorkingState.BranchChildCount;
    private const int FullMask = (1 << BranchChildCount) - 1;
    private const int MaxWorkers = 16;

    /// <summary>Sets every entry's key to its value, deleting it when the value is null or empty, and returns the new root.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="entries">Reordered in place; keys must be unique.</param>
    /// <param name="canParallelize">Whether the children of a full branch level with enough entries may be updated in parallel.</param>
    /// <exception cref="MissingTrieNodeException">The store has no node on a changed path.</exception>
    /// <exception cref="InvalidOperationException">Two entries have the same key.</exception>
    public static (ValueHash256 Hash, ulong Pointer) BulkSet(INodeRecordStore store, Hash256? address, (ValueHash256 Hash, ulong Pointer) root,
        in ArrayPoolListRef<BulkSetEntry> entries, bool canParallelize)
    {
        if (entries.Count == 0) return (root.Hash, NodeRecordCodec.PersistentPointer(root.Pointer));

        using ParallelUnbalancedWork.WorkerScope? workers = canParallelize && !RuntimeInformation.IsSingleProcessor
            && entries.Count >= PatriciaTree.MinEntriesToParallelizeThreshold
            ? ParallelUnbalancedWork.BeginWorkerScope(Math.Min(MaxWorkers, RuntimeInformation.ProcessorCount))
            : null;
        using SpareWorkingStates? spares = workers is null ? null : new();
        BulkSetWorkingState workingState = BulkSetWorkingState.Rent();
        try
        {
            Source rootNode;
            TreePath path = TreePath.Empty;
            Updated updated;
            Context context;
            if (entries.Count < InPlaceSortThreshold)
            {
                // The in-place sorts below the threshold need no separate buffer.
                BulkSetEntry[] entriesArray = entries.UnsafeGetInternalArray();
                context = new(store, address, workingState, spares, entriesArray, entriesArray, canParallelize);
                rootNode = context.LoadRoot(root);
                updated = context.Update(entries.AsSpan(), entries.AsSpan(), flipped: false, ref path, rootNode);
            }
            else
            {
                using ArrayPoolListRef<BulkSetEntry> sortBuffer = new(entries.Count, entries.Count);
                context = new(store, address, workingState, spares, entries.UnsafeGetInternalArray(), sortBuffer.UnsafeGetInternalArray(), canParallelize);
                rootNode = context.LoadRoot(root);
                updated = context.Update(entries.AsSpan(), sortBuffer.AsSpan(), flipped: false, ref path, rootNode);
            }

            if (!updated.Changed) return (root.Hash, NodeRecordCodec.PersistentPointer(root.Pointer));
            if (updated.PendingIndex < 0) return (ValueKeccak.EmptyTreeHash, 0);

            ChildRef top = context.Commit(updated.PendingIndex, TreePath.Empty, isRoot: true);
            return (top.Data, top.Pointer);
        }
        finally
        {
            BulkSetWorkingState.Return(workingState);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static Source ThrowMissingNode(Hash256? address, in TreePath path, in ValueHash256 hash) => throw MissingNode(address, path, hash);

    /// <summary>The exception for a node on a trie path that no store holds.</summary>
    internal static MissingTrieNodeException MissingNode(Hash256? address, in TreePath path, in ValueHash256 hash) =>
        new($"Trie node missing hash={hash} address={address} path={path}", address, path, new Hash256(hash));

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNonUniqueKeys() => throw new InvalidOperationException("Non unique entry keys");

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
        public ReadOnlyMemory<byte> LeafValue()
        {
            if (Kind == SourceKind.Record) return Record.LeafValue;

            ReadInlineLeaf(out ReadOnlySpan<byte> value);
            return value.ToArray();
        }

        private ReadOnlySpan<byte> ReadInlineLeaf(out ReadOnlySpan<byte> value)
        {
            RlpReader reader = new(Inline.Bytes[..InlineLength]);
            reader.ReadSequenceLength();
            ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
            value = reader.DecodeByteArraySpan();
            return hexPrefix;
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

    /// <summary>A branch level whose sixteen children are updated in parallel, each by a task that commits the child's subtree in a working state of its own, so only committed references cross threads.</summary>
    private sealed class ParallelChildren
    {
        public bool Flipped;
        public TreePath Path;
        public int RemainderNibble;
        public Source Remainder;

        /// <summary>Each child's entries in the entries' array, or in the sort buffer's array when <see cref="Flipped"/>.</summary>
        public FixedArray16<(int Start, int Count)> Ranges;

        /// <summary>The references to the children before the join, and the committed references of those in <see cref="ChangedMask"/> after it.</summary>
        public FixedArray16<ChildRef> Children;

        public int ChangedMask;
    }

    /// <summary>The working states of one traversal's parallel children, reused by later children and returned to the pool once the traversal ends.</summary>
    /// <remarks>A reused working state keeps the records it loaded, so every record loaded in the traversal stays readable until it ends.</remarks>
    private sealed class SpareWorkingStates : IDisposable
    {
        private readonly ConcurrentBag<BulkSetWorkingState> _states = [];

        public BulkSetWorkingState Rent() => _states.TryTake(out BulkSetWorkingState? state) ? state : BulkSetWorkingState.Rent();

        public void Return(BulkSetWorkingState state)
        {
            state.ClearPending();
            _states.Add(state);
        }

        public void Dispose()
        {
            foreach (BulkSetWorkingState state in _states) BulkSetWorkingState.Return(state);
        }
    }

    /// <summary>What a bulk set on one working state shares: the store it reads and writes, the trie it writes and the arrays its entries are in.</summary>
    /// <param name="spares">The working states of parallel children, or null when the traversal does not parallelize.</param>
    /// <param name="entries">The array of the entries passed in.</param>
    /// <param name="sortBuffer">The array of the sort buffer, the same as <paramref name="entries"/> when there is none.</param>
    private readonly struct Context(INodeRecordStore store, Hash256? address, BulkSetWorkingState workingState, SpareWorkingStates? spares,
        BulkSetEntry[] entries, BulkSetEntry[] sortBuffer, bool canParallelize)
    {
        private readonly INodeRecordStore _store = store;
        private readonly Hash256? _address = address;
        private readonly BulkSetWorkingState _workingState = workingState;
        private readonly SpareWorkingStates? _spares = spares;
        private readonly BulkSetEntry[] _entries = entries;
        private readonly BulkSetEntry[] _sortBuffer = sortBuffer;
        private readonly bool _canParallelize = canParallelize;

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
            // The child refs of the branch being built: sixteen empty ones under an empty node, those a split leaf or extension
            // leaves, or, for a branch record, a copy made only once a child changes; until then they are read from the record.
            int childrenStart = node.Kind == SourceKind.None ? _workingState.ReserveChildSpace()
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
                    // Nibbles go in ascending order and each writes only its own slot, so a copied slot still holds the record's ref.
                    Source child = isRemainder ? remainder
                        : Load(path, childrenStart >= 0 ? _workingState.Children[childrenStart + nibble] : node.Record.View.Child(nibble));
                    Updated updated = Update(entries[start..end], sortBuffer[start..end], flipped, ref path, child);
                    if (!updated.Changed) continue;

                    reference = updated.Reference;
                }

                changed = true;
                if (isRemainder) remainderKept = false;
                if (reference.Kind == ChildRefKind.Empty) hasRemove = true;
                else nonEmptyCount++;

                if (childrenStart < 0) childrenStart = CopyChildren(node.Record);
                _workingState.Children[childrenStart + nibble] = reference;
            }

            path.TruncateOne();

            // Unlike PatriciaTree.MaybeCombineNode, there is no node to compare with the original: a fake branch whose children
            // all stay is the original, and anything else changes some key under it.
            if (!changed) return Updated.Unchanged(node);

            // A remainder no entry changed stays below the new branch, with the key the branch shortened.
            if (remainderKept) _workingState.Children[childrenStart + remainderNibble] = ChildRef.ForPending(IntoNode(remainder));

            int pendingIndex = (hasRemove || isNew) && nonEmptyCount < 2
                ? MaybeCombine(childrenStart, ref path)
                : _workingState.AddPendingBranch(childrenStart);
            return Updated.To(pendingIndex);
        }

        private bool IsParallel(int count, int mask) =>
            _canParallelize && mask == FullMask && count >= PatriciaTree.MinEntriesToParallelizeThreshold && !RuntimeInformation.IsSingleProcessor;

        /// <summary>Updates the sixteen children of <paramref name="node"/> at <paramref name="path"/> in parallel into <paramref name="level"/>, as the serial loop of <see cref="Update"/> would.</summary>
        /// <remarks>An unchanged remainder is left to the caller, which keeps it pending below the new branch.</remarks>
        private void UpdateInParallel(ParallelChildren level, Span<BulkSetEntry> entries, bool flipped, in TreePath path, ReadOnlySpan<int> indexes,
            in Source node, int childrenStart, int remainderNibble, in Source remainder)
        {
            int offset = entries.OffsetIn(flipped ? _sortBuffer : _entries);
            level.Flipped = flipped;
            level.Path = path;
            level.RemainderNibble = remainderNibble;
            level.Remainder = remainder;
            for (int nibble = 0; nibble < BranchChildCount; nibble++)
            {
                int end = nibble == BranchChildCount - 1 ? entries.Length : indexes[nibble + 1];
                level.Ranges[nibble] = (offset + indexes[nibble], end - indexes[nibble]);
                if (nibble != remainderNibble) level.Children[nibble] = childrenStart >= 0 ? _workingState.Children[childrenStart + nibble] : node.Record.View.Child(nibble);
            }

            ParallelUnbalancedWork.For(0, BranchChildCount, ParallelUnbalancedWork.DefaultOptions, (Context: this, Level: level),
                static (nibble, state) =>
                {
                    state.Context.UpdateChild(state.Level, nibble);
                    return state;
                });
        }

        /// <summary>Updates child <paramref name="nibble"/> of <paramref name="level"/> in a working state of its own, committing it when it changed.</summary>
        private void UpdateChild(ParallelChildren level, int nibble)
        {
            SpareWorkingStates spares = _spares!;
            BulkSetWorkingState workingState = spares.Rent();
            try
            {
                Context context = new(_store, _address, workingState, spares, _entries, _sortBuffer, _canParallelize);
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
                spares.Return(workingState);
            }
        }

        /// <summary>Turns the branch at <paramref name="path"/> left with fewer than two children into nothing, an extension, or its only child with the nibble prepended.</summary>
        /// <returns>The pending node replacing the branch, or -1 when it is removed.</returns>
        private int MaybeCombine(int childrenStart, ref TreePath path)
        {
            int onlyChild = -1;
            for (int i = 0; i < BranchChildCount; i++)
            {
                if (_workingState.Children[childrenStart + i].Kind == ChildRefKind.Empty) continue;
                if (onlyChild >= 0) return _workingState.AddPendingBranch(childrenStart);

                onlyChild = i;
            }

            if (onlyChild < 0) return -1;

            ReadOnlySpan<byte> prefix = [(byte)onlyChild];
            ChildRef child = _workingState.Children[childrenStart + onlyChild];
            if (child.Kind == ChildRefKind.Pending) return PrependKeyPrefix(prefix, child.PendingIndex);

            path.AppendMut(onlyChild);
            Source source = Load(path, child);
            path.TruncateOne();
            // A branch keeps its path, so its reference with the buffer pointer moves under the extension; anything else is re-encoded shorter.
            return source.IsBranch ? _workingState.AddPendingExtension(prefix, child) : PrependKeyPrefix(prefix, IntoNode(source));
        }

        /// <summary>Splits a leaf or an extension into the children of a new branch: its child when its key is a single nibble, otherwise what remains of it below its first nibble.</summary>
        /// <returns>The index of the branch's children.</returns>
        private int MakeFakeBranch(in Source source, out int remainderNibble, out Source remainder)
        {
            int childrenStart = _workingState.ReserveChildSpace();
            Span<byte> key = stackalloc byte[MaxKeyNibbles];
            int keyLength = source.CopyKey(key);
            if (source.NodeKind == NodeRecordKind.Extension && keyLength == 1)
            {
                ChildRef child = source.Record.View.Child(0);
                _workingState.Children[childrenStart + key[0]] = child;
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
                ? _workingState.AddPendingExtension(key[..keyLength], source.Record.View.Child(0))
                : _workingState.AddPendingLeaf(key[..keyLength], source.LeafValue());
        }

        /// <returns>The index of a copy of the branch's sixteen children.</returns>
        private int CopyChildren(NodeRecord branch)
        {
            int childrenStart = _workingState.ReserveChildSpace();
            branch.View.CopyChildren(_workingState.Children.Slice(childrenStart, BranchChildCount));
            return childrenStart;
        }

        /// <summary>The root node, read by its supplied pointer like any child.</summary>
        /// <exception cref="MissingTrieNodeException">The store has no such node.</exception>
        public Source LoadRoot(in (ValueHash256 Hash, ulong Pointer) root) =>
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
            if (_store.TryLocate(child.Pointer, out NodeRecord record) || _workingState.Loaded.TryLoad(_store, path, child.Data, out record))
                return Source.ForRecord(record, 0);

            return ThrowMissingNode(_address, path, child.Data);
        }

        /// <summary>Applies a single entry to <paramref name="node"/> at <paramref name="path"/>, like <see cref="PatriciaTree"/>'s set: walks down to where the entry lands, then rebuilds the levels it passed on the way back up.</summary>
        /// <remarks>A branch record walked through is copied out only when the child it walked into changed.</remarks>
        private Updated SetOne(in BulkSetEntry entry, ref TreePath path, in Source node)
        {
            Span<byte> entryKey = stackalloc byte[MaxKeyNibbles];
            Span<byte> key = stackalloc byte[MaxKeyNibbles];
            Nibbles.BytesToNibbleBytes(entry.Path.Bytes, entryKey);
            byte[]? value = entry.Value;
            bool isDelete = value is null || value.Length == 0;
            Span<SetOneFrame> frames = _workingState.Frames;
            int frameCount = 0;
            Source current = node;
            Updated result;
            while (true)
            {
                int depth = path.Length;
                if (current.Kind == SourceKind.None)
                {
                    result = isDelete ? Updated.Unchanged(current) : Updated.To(_workingState.AddPendingLeaf(entryKey[depth..], value!));
                    break;
                }

                if (current.NodeKind == NodeRecordKind.Leaf)
                {
                    int keyLength = current.CopyKey(key);
                    int matched = key[..keyLength].CommonPrefixLength(entryKey[depth..]);
                    if (matched == keyLength)
                    {
                        result = isDelete ? Updated.To(-1)
                            : current.LeafValue().Span.SequenceEqual(value) ? Updated.Unchanged(current)
                            : Updated.To(_workingState.AddPendingLeaf(key[..keyLength], value!));
                    }
                    else
                    {
                        result = isDelete ? Updated.Unchanged(current)
                            : Updated.To(DivergingBranch(_workingState, entryKey, depth, matched, key[matched],
                                ChildRef.ForPending(_workingState.AddPendingLeaf(key[(matched + 1)..keyLength], current.LeafValue())), value!));
                    }

                    break;
                }

                NodeRecordRef view = current.Record.View;
                if (current.NodeKind == NodeRecordKind.Extension)
                {
                    int keyLength = current.CopyKey(key);
                    int matched = key[..keyLength].CommonPrefixLength(entryKey[depth..]);
                    if (matched < keyLength)
                    {
                        if (isDelete)
                        {
                            result = Updated.Unchanged(current);
                            break;
                        }

                        // When the extension's key ends at the divergence, its child moves under the new branch at the same path, so it is not loaded.
                        ReadOnlySpan<byte> rest = key[(matched + 1)..keyLength];
                        ChildRef existing = rest.IsEmpty ? view.Child(0) : ChildRef.ForPending(_workingState.AddPendingExtension(rest, view.Child(0)));
                        result = Updated.To(DivergingBranch(_workingState, entryKey, depth, matched, key[matched], existing, value!));
                        break;
                    }

                    path.AppendMut(key[..keyLength]);
                    frames[frameCount++] = new SetOneFrame(current.Record, current.Skip, 0);
                    current = Load(path, view.Child(0));
                }
                else
                {
                    byte nibble = entryKey[depth];
                    path.AppendMut(nibble);
                    frames[frameCount++] = new SetOneFrame(current.Record, 0, nibble);
                    current = Load(path, view.Child(nibble));
                }
            }

            while (frameCount > 0)
            {
                SetOneFrame frame = frames[--frameCount];
                frames[frameCount] = default;
                Source source = Source.ForRecord(frame.Record, frame.Skip);
                if (source.NodeKind == NodeRecordKind.Extension)
                {
                    int keyLength = source.CopyKey(key);
                    path.TruncateMut(path.Length - keyLength);
                    result = !result.Changed ? Updated.Unchanged(source)
                        : result.PendingIndex < 0 ? result
                        : Updated.To(PrependKeyPrefix(key[..keyLength], result.PendingIndex));
                }
                else
                {
                    path.TruncateOne();
                    result = !result.Changed ? Updated.Unchanged(source)
                        : Updated.To(ReplaceChild(CopyChildren(frame.Record), frame.ChildNibble, result.PendingIndex, ref path));
                }
            }

            return result;

            // The branch where the entry diverges from an existing leaf or extension after matched nibbles, holding existing under
            // existingNibble, behind an extension of the matched nibbles when there are any.
            static int DivergingBranch(BulkSetWorkingState workingState, ReadOnlySpan<byte> entryKey, int depth, int matched, int existingNibble, ChildRef existing, byte[] value)
            {
                int branchDepth = depth + matched;
                int leaf = workingState.AddPendingLeaf(entryKey[(branchDepth + 1)..], value);
                int childrenStart = workingState.ReserveChildSpace();
                workingState.Children[childrenStart + existingNibble] = existing;
                workingState.Children[childrenStart + entryKey[branchDepth]] = ChildRef.ForPending(leaf);
                int branch = workingState.AddPendingBranch(childrenStart);
                return matched == 0 ? branch : workingState.AddPendingExtension(entryKey.Slice(depth, matched), ChildRef.ForPending(branch));
            }
        }

        /// <summary>Puts <paramref name="prefix"/> in front of the pending node at <paramref name="index"/>, merging into its key unless it is a branch.</summary>
        private int PrependKeyPrefix(ReadOnlySpan<byte> prefix, int index)
        {
            ref PendingNode node = ref _workingState.Nodes[index];
            if (node.Kind == PendingKind.Branch) return _workingState.AddPendingExtension(prefix, ChildRef.ForPending(index));

            Span<byte> key = node.Key;
            key[..node.KeyLength].CopyTo(key[prefix.Length..]);
            prefix.CopyTo(key);
            node.KeyLength += (byte)prefix.Length;
            return index;
        }

        /// <summary>Puts the pending node at <paramref name="index"/>, -1 when removed, as child <paramref name="nibble"/> of the branch at <paramref name="path"/>, collapsing the branch when the child was removed.</summary>
        private int ReplaceChild(int childrenStart, int nibble, int index, ref TreePath path)
        {
            if (index >= 0)
            {
                _workingState.Children[childrenStart + nibble] = ChildRef.ForPending(index);
                return _workingState.AddPendingBranch(childrenStart);
            }

            _workingState.Children[childrenStart + nibble] = default;
            return MaybeCombine(childrenStart, ref path);
        }

        /// <summary>Encodes and hashes the pending node at <paramref name="top"/> and every pending node below it, writing a record of each one referenced by hash, that is, whose RLP is at least 32 bytes or which is the root.</summary>
        /// <remarks>
        /// Nodes are committed one path depth at a time, deepest first, like <see cref="DirtyNodeHasher"/>: two nodes of one depth are never
        /// each other's ancestor, and by then all their children are resolved, so a level is reserved in the buffer at once, encoded in place
        /// with its children's pointers and hashed together.
        /// </remarks>
        /// <returns>The reference to the top node.</returns>
        public ChildRef Commit(int top, in TreePath path, bool isRoot)
        {
            int count = CollectPending(top, path);
            Span<int> levelEnds = stackalloc int[MaxKeyNibbles + 2];
            Span<int> positions = stackalloc int[MaxKeyNibbles + 2];
            Span<int> collected = _workingState.Collected;
            Span<int> order = _workingState.Order;
            for (int i = 0; i < count; i++)
            {
                levelEnds[_workingState.Nodes[collected[i]].Path.Length]++;
            }

            // Suffix sums, so depth d takes [levelEnds[d + 1], levelEnds[d]) of the order.
            for (int depth = MaxKeyNibbles; depth >= 0; depth--)
            {
                levelEnds[depth] += levelEnds[depth + 1];
            }

            levelEnds.CopyTo(positions);
            for (int i = 0; i < count; i++)
            {
                order[--positions[_workingState.Nodes[collected[i]].Path.Length]] = collected[i];
            }

            ChildRef topReference = default;
            for (int depth = MaxKeyNibbles; depth >= 0; depth--)
            {
                CommitLevel(order[levelEnds[depth + 1]..levelEnds[depth]], top, isRoot, ref topReference);
            }

            return topReference;
        }

        /// <summary>Lists the pending node at <paramref name="top"/> and its pending descendants in <see cref="BulkSetWorkingState.Collected"/>, setting their path, parent and slot.</summary>
        /// <returns>The number listed.</returns>
        private int CollectPending(int top, in TreePath path)
        {
            _workingState.EnsureCommitCapacity(_workingState.NodeCount);
            Span<int> collected = _workingState.Collected;
            _workingState.Nodes[top].Path = path;
            collected[0] = top;
            int count = 1;
            for (int head = 0; head < count; head++)
            {
                int parent = collected[head];
                ref PendingNode node = ref _workingState.Nodes[parent];
                if (node.Kind == PendingKind.Extension)
                {
                    if (node.ExtensionChild.Kind == ChildRefKind.Pending) collected[count++] = SetPosition(node.ExtensionChild.PendingIndex, node.Path.Append(node.KeyNibbles), parent, 0);
                }
                else if (node.Kind == PendingKind.Branch)
                {
                    for (int slot = 0; slot < BranchChildCount; slot++)
                    {
                        ref ChildRef child = ref _workingState.Children[node.ChildrenStart + slot];
                        if (child.Kind == ChildRefKind.Pending) collected[count++] = SetPosition(child.PendingIndex, node.Path.Append(slot), parent, slot);
                    }
                }
            }

            return count;
        }

        private int SetPosition(int index, in TreePath path, int parent, int slot)
        {
            ref PendingNode node = ref _workingState.Nodes[index];
            node.Path = path;
            node.Parent = parent;
            node.Slot = (byte)slot;
            return index;
        }

        private void CommitLevel(ReadOnlySpan<int> level, int top, bool isRoot, ref ChildRef topReference)
        {
            Span<int> hashed = _workingState.Hashed;
            Span<int> recordStarts = _workingState.RecordStarts;
            Span<(int Start, int Length)> messages = _workingState.Messages;
            Span<byte> inlineRlp = stackalloc byte[ValueHash256.MemorySize];
            int hashedCount = 0;
            int totalLength = 0;
            foreach (int index in level)
            {
                ref PendingNode node = ref _workingState.Nodes[index];
                int rlpLength = RlpLength(ref node);
                if (rlpLength < ValueHash256.MemorySize && !(isRoot && index == top))
                {
                    Encode(ref node, inlineRlp);
                    Link(index, top, ChildRef.ForInline(inlineRlp[..rlpLength]), ref topReference);
                    continue;
                }

                hashed[hashedCount] = index;
                recordStarts[hashedCount] = totalLength;
                messages[hashedCount] = (0, rlpLength);
                totalLength += NodeRecordCodec.AlignedLength(NodeRecordCodec.Length(HashChildCount(ref node), rlpLength));
                hashedCount++;
            }

            if (hashedCount == 0) return;

            using INodeRecordWriteBatch batch = _store.StartWriteBatch(totalLength);
            Span<byte> buffer = batch.Bytes;
            ulong basePointer = batch.BasePointer;
            for (int i = 0; i < hashedCount; i++)
            {
                ref PendingNode node = ref _workingState.Nodes[hashed[i]];
                int rlpOffset = WriteRecord(ref node, buffer[recordStarts[i]..], messages[i].Length, NodeRecordCodec.NodeKeyHash(_address, node.Path));
                messages[i] = (recordStarts[i] + rlpOffset, messages[i].Length);
            }

            Span<ValueHash256> hashes = _workingState.Hashes[..hashedCount];
            KeccakBatch.Hash(buffer, messages[..hashedCount], hashes);
            for (int i = 0; i < hashedCount; i++)
            {
                hashes[i].Bytes.CopyTo(buffer[(recordStarts[i] + NodeRecordCodec.HashOffset)..]);
                ulong pointer = basePointer + (ulong)recordStarts[i];
                Link(hashed[i], top, ChildRef.ForHash(hashes[i], pointer), ref topReference);
            }
        }

        private void Link(int index, int top, in ChildRef reference, ref ChildRef topReference)
        {
            if (index == top)
            {
                topReference = reference;
                return;
            }

            ref PendingNode node = ref _workingState.Nodes[index];
            ref PendingNode parent = ref _workingState.Nodes[node.Parent];
            if (parent.Kind == PendingKind.Extension) parent.ExtensionChild = reference;
            else _workingState.Children[parent.ChildrenStart + node.Slot] = reference;
        }

        /// <summary>Writes the record of <paramref name="node"/>, all but its hash, into the zero-filled <paramref name="record"/>.</summary>
        /// <returns>The offset of the RLP in the record.</returns>
        private int WriteRecord(ref PendingNode node, Span<byte> record, int rlpLength, ulong nodeKeyHash)
        {
            ReadOnlySpan<ChildRef> children = node.Kind switch
            {
                PendingKind.Branch => _workingState.Children.Slice(node.ChildrenStart, BranchChildCount),
                PendingKind.Extension => new ReadOnlySpan<ChildRef>(ref node.ExtensionChild),
                _ => [],
            };
            NodeRecordKind kind = node.Kind switch
            {
                PendingKind.Leaf => NodeRecordKind.Leaf,
                PendingKind.Extension => NodeRecordKind.Extension,
                _ => NodeRecordKind.HashedBranch,
            };

            ushort hashMask = 0;
            int pointerOffset = NodeRecordCodec.HeaderLength;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].Kind == ChildRefKind.Inline && node.Kind == PendingKind.Branch) kind = NodeRecordKind.InlineBranch;
                if (children[i].Kind != ChildRefKind.Hash) continue;

                hashMask |= (ushort)(1 << i);
                BinaryPrimitives.WriteUInt64LittleEndian(record[pointerOffset..], NodeRecordCodec.PersistentPointer(children[i].Pointer));
                pointerOffset += NodeRecordCodec.PointerLength;
            }

            record[0] = (byte)kind;
            BinaryPrimitives.WriteUInt16LittleEndian(record[NodeRecordCodec.HashMaskOffset..], hashMask);
            BinaryPrimitives.WriteUInt32LittleEndian(record[NodeRecordCodec.RlpLengthOffset..], (uint)rlpLength);
            BinaryPrimitives.WriteUInt64LittleEndian(record[NodeRecordCodec.NodeKeyHashOffset..], nodeKeyHash);
            Encode(ref node, record[pointerOffset..]);
            return pointerOffset;
        }

        private int RlpLength(ref PendingNode node) => node.Kind switch
        {
            PendingKind.Leaf => NodeRlp.LeafLength(node.KeyNibbles, node.Value.Span),
            PendingKind.Extension => NodeRlp.ExtensionLength(node.KeyNibbles, node.ExtensionChild),
            _ => NodeRlp.BranchLength(_workingState.Children.Slice(node.ChildrenStart, BranchChildCount)),
        };

        private int Encode(ref PendingNode node, Span<byte> destination) => node.Kind switch
        {
            PendingKind.Leaf => NodeRlp.WriteLeaf(node.KeyNibbles, node.Value.Span, destination),
            PendingKind.Extension => NodeRlp.WriteExtension(node.KeyNibbles, node.ExtensionChild, destination),
            _ => NodeRlp.WriteBranch(_workingState.Children.Slice(node.ChildrenStart, BranchChildCount), destination),
        };

        private int HashChildCount(ref PendingNode node)
        {
            if (node.Kind == PendingKind.Extension) return node.ExtensionChild.Kind == ChildRefKind.Hash ? 1 : 0;
            if (node.Kind != PendingKind.Branch) return 0;

            int count = 0;
            foreach (ChildRef child in _workingState.Children.Slice(node.ChildrenStart, BranchChildCount))
            {
                if (child.Kind == ChildRefKind.Hash) count++;
            }

            return count;
        }
    }
}
