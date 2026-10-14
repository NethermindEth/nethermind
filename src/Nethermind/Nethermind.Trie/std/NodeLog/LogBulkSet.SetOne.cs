// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BulkSetEntry = Nethermind.Trie.PatriciaTree.BulkSetEntry;

namespace Nethermind.Trie.NodeLog;

internal static partial class LogBulkSet
{
    private readonly partial struct Context
    {
        /// <summary>Applies a single entry to <paramref name="node"/> at <paramref name="path"/>, like <see cref="PatriciaTree"/>'s set: walks down to where the entry lands, then rebuilds the levels it passed on the way back up.</summary>
        /// <remarks>A branch record walked through is copied out only when the child it walked into changed.</remarks>
        private Updated SetOne(in BulkSetEntry entry, ref TreePath path, in Source node)
        {
            Span<byte> entryKey = stackalloc byte[MaxKeyNibbles];
            Span<byte> key = stackalloc byte[MaxKeyNibbles];
            Nibbles.BytesToNibbleBytes(entry.Path.Bytes, entryKey);
            byte[]? value = entry.Value;
            bool isDelete = value is null || value.Length == 0;
            SetOneFrame[] frames = _arena.Frames;
            int frameCount = 0;
            Source current = node;
            Updated result;
            while (true)
            {
                int depth = path.Length;
                if (current.Kind == SourceKind.None)
                {
                    result = isDelete ? Updated.Unchanged(current) : Updated.To(_arena.AddLeaf(entryKey[depth..], new ValueRef(value!, 0, value!.Length)));
                    break;
                }

                if (current.NodeKind == NodeRecordKind.Leaf)
                {
                    result = SetLeaf(entryKey, depth, current, value);
                    break;
                }

                RecordView view = current.Record.View;
                if (current.NodeKind == NodeRecordKind.Extension)
                {
                    int keyLength = current.CopyKey(key);
                    int matched = key[..keyLength].CommonPrefixLength(entryKey[depth..]);
                    if (matched < keyLength)
                    {
                        result = isDelete
                            ? Updated.Unchanged(current)
                            : Updated.To(DivergeExtension(entryKey, depth, matched, key[..keyLength], view.Child(0), value!));
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
                        : Updated.To(Prepend(key[..keyLength], result.PendingIndex));
                }
                else
                {
                    path.TruncateOne();
                    result = !result.Changed ? Updated.Unchanged(source)
                        : Updated.To(ReplaceChild(CopyChildren(frame.Record), frame.ChildNibble, result.PendingIndex, ref path));
                }
            }

            return result;
        }

        /// <summary>Sets the key <paramref name="entryKey"/> to <paramref name="value"/> in <paramref name="leaf"/> at <paramref name="depth"/>.</summary>
        private Updated SetLeaf(ReadOnlySpan<byte> entryKey, int depth, in Source leaf, byte[]? value)
        {
            Span<byte> key = stackalloc byte[MaxKeyNibbles];
            int keyLength = leaf.CopyKey(key);
            int matched = key[..keyLength].CommonPrefixLength(entryKey[depth..]);
            bool isDelete = value is null || value.Length == 0;
            if (matched == keyLength)
            {
                if (isDelete) return Updated.To(-1);

                return leaf.LeafValue().Span.SequenceEqual(value)
                    ? Updated.Unchanged(leaf)
                    : Updated.To(_arena.AddLeaf(key[..keyLength], new ValueRef(value!, 0, value!.Length)));
            }

            if (isDelete) return Updated.Unchanged(leaf);

            ChildRef existing = ChildRef.ForPending(_arena.AddLeaf(key[(matched + 1)..keyLength], leaf.LeafValue()));
            return Updated.To(DivergingBranch(entryKey, depth, matched, key[matched], existing, value!));
        }

        /// <summary>The node replacing the extension at <paramref name="depth"/> with <paramref name="key"/> and <paramref name="child"/>, from which the entry diverges after <paramref name="matched"/> nibbles.</summary>
        /// <remarks>When the extension's key ends at the divergence, its child moves under the new branch at the same path, so it is not loaded.</remarks>
        private int DivergeExtension(ReadOnlySpan<byte> entryKey, int depth, int matched, ReadOnlySpan<byte> key, in ChildRef child, byte[] value)
        {
            ReadOnlySpan<byte> rest = key[(matched + 1)..];
            ChildRef existing = rest.IsEmpty ? child : ChildRef.ForPending(_arena.AddExtension(rest, child));
            return DivergingBranch(entryKey, depth, matched, key[matched], existing, value);
        }

        /// <summary>The branch where the entry diverges from an existing leaf or extension after <paramref name="matched"/> nibbles, holding <paramref name="existing"/> under <paramref name="existingNibble"/>, behind an extension of the matched nibbles when there are any.</summary>
        private int DivergingBranch(ReadOnlySpan<byte> entryKey, int depth, int matched, int existingNibble, ChildRef existing, byte[] value)
        {
            int branchDepth = depth + matched;
            int leaf = _arena.AddLeaf(entryKey[(branchDepth + 1)..], new ValueRef(value, 0, value.Length));
            int childrenStart = _arena.AddChildren();
            _arena.Children[childrenStart + existingNibble] = existing;
            _arena.Children[childrenStart + entryKey[branchDepth]] = ChildRef.ForPending(leaf);
            int branch = _arena.AddBranch(childrenStart);
            return matched == 0 ? branch : _arena.AddExtension(entryKey.Slice(depth, matched), ChildRef.ForPending(branch));
        }

        /// <summary>Puts <paramref name="prefix"/> in front of the pending node at <paramref name="index"/>, merging into its key unless it is a branch.</summary>
        private int Prepend(ReadOnlySpan<byte> prefix, int index)
        {
            ref PendingNode node = ref _arena.Nodes[index];
            if (node.Kind == PendingKind.Branch) return _arena.AddExtension(prefix, ChildRef.ForPending(index));

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
                _arena.Children[childrenStart + nibble] = ChildRef.ForPending(index);
                return _arena.AddBranch(childrenStart);
            }

            _arena.Children[childrenStart + nibble] = default;
            return MaybeCombine(childrenStart, ref path);
        }
    }
}
