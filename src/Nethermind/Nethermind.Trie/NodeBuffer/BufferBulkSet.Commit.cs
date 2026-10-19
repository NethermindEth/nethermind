// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

internal static partial class BufferBulkSet
{
    private readonly partial struct Context
    {
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
            int[] collected = _arena.Collected;
            int[] order = _arena.Order;
            for (int i = 0; i < count; i++)
            {
                levelEnds[_arena.Nodes[collected[i]].Path.Length]++;
            }

            // Suffix sums, so depth d takes [levelEnds[d + 1], levelEnds[d]) of the order.
            for (int depth = MaxKeyNibbles; depth >= 0; depth--)
            {
                levelEnds[depth] += levelEnds[depth + 1];
            }

            levelEnds.CopyTo(positions);
            for (int i = 0; i < count; i++)
            {
                order[--positions[_arena.Nodes[collected[i]].Path.Length]] = collected[i];
            }

            ChildRef topReference = default;
            for (int depth = MaxKeyNibbles; depth >= 0; depth--)
            {
                CommitLevel(order.AsSpan(levelEnds[depth + 1]..levelEnds[depth]), top, isRoot, ref topReference);
            }

            return topReference;
        }

        /// <summary>Lists the pending node at <paramref name="top"/> and its pending descendants in <see cref="BulkSetArena.Collected"/>, setting their path, parent and slot.</summary>
        /// <returns>The number listed.</returns>
        private int CollectPending(int top, in TreePath path)
        {
            _arena.EnsureCommitCapacity(_arena.NodeCount);
            int[] collected = _arena.Collected;
            _arena.Nodes[top].Path = path;
            collected[0] = top;
            int count = 1;
            for (int head = 0; head < count; head++)
            {
                int parent = collected[head];
                ref PendingNode node = ref _arena.Nodes[parent];
                if (node.Kind == PendingKind.Extension)
                {
                    if (node.ExtensionChild.Kind == ChildRefKind.Pending) collected[count++] = SetPosition(node.ExtensionChild.PendingIndex, node.Path.Append(node.KeyNibbles), parent, 0);
                }
                else if (node.Kind == PendingKind.Branch)
                {
                    for (int slot = 0; slot < BranchChildCount; slot++)
                    {
                        ref ChildRef child = ref _arena.Children[node.ChildrenStart + slot];
                        if (child.Kind == ChildRefKind.Pending) collected[count++] = SetPosition(child.PendingIndex, node.Path.Append(slot), parent, slot);
                    }
                }
            }

            return count;
        }

        private int SetPosition(int index, in TreePath path, int parent, int slot)
        {
            ref PendingNode node = ref _arena.Nodes[index];
            node.Path = path;
            node.Parent = parent;
            node.Slot = (byte)slot;
            return index;
        }

        private void CommitLevel(ReadOnlySpan<int> level, int top, bool isRoot, ref ChildRef topReference)
        {
            int[] hashed = _arena.Hashed;
            int[] recordStarts = _arena.RecordStarts;
            (int Start, int Length)[] messages = _arena.Messages;
            Span<byte> inlineRlp = stackalloc byte[ValueHash256.MemorySize];
            int hashedCount = 0;
            int totalLength = 0;
            foreach (int index in level)
            {
                ref PendingNode node = ref _arena.Nodes[index];
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
                ref PendingNode node = ref _arena.Nodes[hashed[i]];
                int rlpOffset = WriteRecord(ref node, buffer[recordStarts[i]..], messages[i].Length, NodeRecordCodec.NodeKey(_address, node.Path));
                messages[i] = (recordStarts[i] + rlpOffset, messages[i].Length);
            }

            Span<ValueHash256> hashes = _arena.Hashes.AsSpan(0, hashedCount);
            KeccakBatch.Hash(buffer, messages.AsSpan(0, hashedCount), hashes);
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

            ref PendingNode node = ref _arena.Nodes[index];
            ref PendingNode parent = ref _arena.Nodes[node.Parent];
            if (parent.Kind == PendingKind.Extension) parent.ExtensionChild = reference;
            else _arena.Children[parent.ChildrenStart + node.Slot] = reference;
        }

        /// <summary>Writes the record of <paramref name="node"/>, all but its hash, into the zero-filled <paramref name="record"/>.</summary>
        /// <returns>The offset of the RLP in the record.</returns>
        private int WriteRecord(ref PendingNode node, Span<byte> record, int rlpLength, ulong nodeKey)
        {
            ReadOnlySpan<ChildRef> children = node.Kind switch
            {
                PendingKind.Branch => _arena.Children.AsSpan(node.ChildrenStart, BranchChildCount),
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
            BinaryPrimitives.WriteUInt64LittleEndian(record[NodeRecordCodec.NodeKeyOffset..], nodeKey);
            Encode(ref node, record[pointerOffset..]);
            return pointerOffset;
        }

        private int RlpLength(ref PendingNode node) => node.Kind switch
        {
            PendingKind.Leaf => NodeRlp.LeafLength(node.KeyNibbles, node.Value.Span),
            PendingKind.Extension => NodeRlp.ExtensionLength(node.KeyNibbles, node.ExtensionChild),
            _ => NodeRlp.BranchLength(_arena.Children.AsSpan(node.ChildrenStart, BranchChildCount)),
        };

        private int Encode(ref PendingNode node, Span<byte> destination) => node.Kind switch
        {
            PendingKind.Leaf => NodeRlp.WriteLeaf(node.KeyNibbles, node.Value.Span, destination),
            PendingKind.Extension => NodeRlp.WriteExtension(node.KeyNibbles, node.ExtensionChild, destination),
            _ => NodeRlp.WriteBranch(_arena.Children.AsSpan(node.ChildrenStart, BranchChildCount), destination),
        };

        private int HashChildCount(ref PendingNode node)
        {
            if (node.Kind == PendingKind.Extension) return node.ExtensionChild.Kind == ChildRefKind.Hash ? 1 : 0;
            if (node.Kind != PendingKind.Branch) return 0;

            int count = 0;
            foreach (ChildRef child in _arena.Children.AsSpan(node.ChildrenStart, BranchChildCount))
            {
                if (child.Kind == ChildRefKind.Hash) count++;
            }

            return count;
        }
    }
}
