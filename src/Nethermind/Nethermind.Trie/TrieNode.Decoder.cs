// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Buffers;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        // Used to create the nibble key from bytes, and threshold before using ArrayPool for the key
        private const int StackallocByteThreshold = 384;

        private class TrieNodeDecoder
        {
            [SkipLocalsInit]
            public static CappedArray<byte> EncodeExtension(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                Metrics.TreeNodeRlpEncodings++;

                Debug.Assert(item.NodeType == NodeType.Extension,
                    $"Node passed to {nameof(EncodeExtension)} is {item.NodeType}");
                Debug.Assert(item.Key is not null,
                    "Extension key is null when encoding");

                byte[] hexPrefix = item.Key;
                int hexLength = HexPrefix.ByteLength(hexPrefix);
                byte[]? rentedBuffer = hexLength > StackallocByteThreshold
                    ? ArrayPool<byte>.Shared.Rent(hexLength)
                    : null;

                Span<byte> keyBytes = (rentedBuffer is null
                    ? stackalloc byte[StackallocByteThreshold]
                    : rentedBuffer)[..hexLength];

                HexPrefix.CopyToSpan(hexPrefix, isLeaf: false, keyBytes);

                // Fast path: child was unresolved (slot null, hash held in parent _rlpArray) —
                // encode the hash directly via TryGetChildHash without materializing a TrieNode.
                TrieNode? nodeRef = null;
                ValueHash256 childKeccak = default;
                bool hasChildKeccak = false;
                TrieNode? extChild = item.GetSlotRef(0);
                if (extChild is null && item.TryGetChildHash(0, out ValueHash256 hashFromRlp))
                {
                    childKeccak = hashFromRlp;
                    hasChildKeccak = true;
                }
                else
                {
                    int previousLength = item.AppendChildPath(ref path, 0);
                    nodeRef = item.GetChildWithChildPath(tree, ref path, 0);
                    Debug.Assert(nodeRef is not null,
                        "Extension child is null when encoding.");

                    nodeRef.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                    path.TruncateMut(previousLength);

                    hasChildKeccak = nodeRef.TryGetKeccak(out childKeccak);
                }

                int contentLength = Rlp.LengthOf(keyBytes) + (hasChildKeccak ? Rlp.LengthOfKeccakRlp : nodeRef!.FullRlp.Length);
                int totalLength = Rlp.LengthOfSequence(contentLength);

                CappedArray<byte> data = bufferPool.SafeRent(totalLength);
                Span<byte> destination = data.AsSpan();
                int position = Rlp.StartSequence(destination, 0, contentLength);
                position = Rlp.Encode(destination, position, keyBytes);

                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
                if (hasChildKeccak)
                {
                    Rlp.Encode(destination, position, in childKeccak);
                }
                else
                {
                    // Inline child: happens with a short extension to a branch with a short extension as the only child
                    // so |
                    // so |
                    // so E - - - - - - - - - - - - - - -
                    // so |
                    // so |
                    nodeRef!.FullRlp.AsSpan().CopyTo(destination.Slice(position));
                }

                return data;
            }

            [SkipLocalsInit]
            public static CappedArray<byte> EncodeLeaf(TrieNode node, ICappedArrayPool? pool)
            {
                Metrics.TreeNodeRlpEncodings++;

                if (node.Key is null)
                {
                    ThrowNullKey(node);
                }

                byte[] hexPrefix = node.Key;
                int hexLength = HexPrefix.ByteLength(hexPrefix);
                byte[]? rentedBuffer = hexLength > StackallocByteThreshold
                    ? ArrayPool<byte>.Shared.Rent(hexLength)
                    : null;

                Span<byte> keyBytes = (rentedBuffer is null
                    ? stackalloc byte[StackallocByteThreshold]
                    : rentedBuffer)[..hexLength];

                HexPrefix.CopyToSpan(hexPrefix, isLeaf: true, keyBytes);
                int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOf(node.Value.AsSpan());
                int totalLength = Rlp.LengthOfSequence(contentLength);

                CappedArray<byte> data = pool.SafeRent(totalLength);
                Span<byte> destination = data.AsSpan();
                int position = Rlp.StartSequence(destination, 0, contentLength);
                position = Rlp.Encode(destination, position, keyBytes);

                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }

                Rlp.Encode(destination, position, node.Value.AsSpan());

                return data;
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowNullKey(TrieNode node) => throw new TrieException($"Hex prefix of a leaf node is null at node {node.Keccak}");

            public static CappedArray<byte> RlpEncodeBranch(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? pool, bool canBeParallel)
            {
                Metrics.TreeNodeRlpEncodings++;

                Debug.Assert(!item.HasRlp || IsValidRetainedBranchRlp(item.FullRlp),
                    "Retained branch RLP should already be validated before branch encoding.");

                if (!UseParallel(canBeParallel, item))
                {
                    return RlpEncodeBranchSerial(item, tree, ref path, pool, canBeParallel);
                }

                const int valueRlpLength = 1;
                int childrenRlpLength = GetChildrenRlpLengthForBranchParallel(tree, ref path, item, pool, canBeParallel);
                int contentLength = valueRlpLength + childrenRlpLength;
                int sequenceLength = Rlp.LengthOfSequence(contentLength);
                CappedArray<byte> result = pool.SafeRent(sequenceLength);
                Span<byte> resultSpan = result.AsSpan();
                int position = Rlp.StartSequence(resultSpan, 0, contentLength);
                WriteChildrenRlpBranch(tree, ref path, item, resultSpan.Slice(position, childrenRlpLength), pool, canBeParallel);
                resultSpan[sequenceLength - valueRlpLength] = Rlp.EmptyByteArrayByte;

                return result;

                static bool UseParallel(bool canBeParallel, TrieNode item)
                {
                    if (Environment.ProcessorCount <= 1 || !canBeParallel)
                    {
                        return false;
                    }

                    const int MinChildrenForParallel = 4;
                    int nonNullChildren = 0;
                    for (int i = 0; i < BranchesCount; i++)
                    {
                        TrieNode? data = item.GetSlotRef(i);
                        if (data is not null && !ReferenceEquals(data, NullNode) && ++nonNullChildren >= MinChildrenForParallel)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            private static bool IsValidRetainedBranchRlp(CappedArray<byte> parentRlp)
            {
                try
                {
                    ValueRlpStream rlpStream = new(parentRlp);
                    int contentEnd = rlpStream.ReadSequenceLength() + rlpStream.Position;
                    for (int i = 0; i < BranchesCount; i++)
                    {
                        rlpStream.SkipItem();
                        if (rlpStream.Position > contentEnd)
                        {
                            return false;
                        }
                    }

                    rlpStream.SkipItem();
                    return rlpStream.Position == contentEnd && contentEnd == rlpStream.Length;
                }
                catch (Exception exception) when (exception is RlpException or ArgumentException or IndexOutOfRangeException)
                {
                    return false;
                }
            }

            private enum BranchChildRlpKind : byte
            {
                Empty,
                ParentRlpSlice,
                Keccak,
                Inline
            }

            private readonly struct BranchChildRlpMetadata(BranchChildRlpKind kind, int length, int parentRlpOffset = 0)
            {
                public readonly BranchChildRlpKind Kind = kind;
                public readonly int Length = length;
                public readonly int ParentRlpOffset = parentRlpOffset;
            }

            [SkipLocalsInit]
            private static CappedArray<byte> RlpEncodeBranchSerial(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? pool, bool canBeParallel)
            {
                const int valueRlpLength = 1;
                Span<BranchChildRlpMetadata> children = stackalloc BranchChildRlpMetadata[BranchesCount];
                CappedArray<byte> parentRlp = item.FullRlp;
                int childrenRlpLength = parentRlp.IsNotNull
                    ? RecordChildrenRlpBranchRlp(tree, ref path, item, parentRlp, children, pool, canBeParallel)
                    : RecordChildrenRlpBranchNonRlp(tree, ref path, item, children, pool, canBeParallel);
                int contentLength = valueRlpLength + childrenRlpLength;
                int sequenceLength = Rlp.LengthOfSequence(contentLength);
                CappedArray<byte> result = pool.SafeRent(sequenceLength);
                Span<byte> resultSpan = result.AsSpan();
                int position = Rlp.StartSequence(resultSpan, 0, contentLength);
                ReadOnlySpan<byte> parentRlpSpan = parentRlp.IsNotNull ? parentRlp.AsSpan() : default;
                WriteRecordedChildrenRlpBranch(item, parentRlpSpan, children, resultSpan.Slice(position, childrenRlpLength));
                resultSpan[sequenceLength - valueRlpLength] = Rlp.EmptyByteArrayByte;

                return result;
            }

            private static int GetChildrenRlpLengthForBranchParallel(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel) =>
                // Tail call optimized.
                item.HasRlp
                    ? GetChildrenRlpLengthForBranchRlpParallel(tree, path, item, bufferPool, canBeParallel)
                    : GetChildrenRlpLengthForBranchNonRlpParallel(tree, path, item, bufferPool, canBeParallel);

            private static int GetChildrenRlpLengthForBranchNonRlpParallel(ITrieNodeResolver tree, TreePath rootPath, TrieNode item, ICappedArrayPool bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ParallelUnbalancedWork.For(0, BranchesCount, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                    (local: 0, item, tree, bufferPool, rootPath, canBeParallel),
                    static (i, state) =>
                    {
                        TrieNode? data = state.item.GetSlotRef(i);
                        if (data is null || ReferenceEquals(data, NullNode))
                        {
                            // Null slot on a non-RLP branch is impossible (no parent RLP to
                            // decode from). Treat as empty.
                            state.local++;
                        }
                        else
                        {
                            TreePath path = state.rootPath;
                            state.local += ResolveAndGetBranchChildRlpLength(state.tree, ref path, i, data, state.bufferPool, state.canBeParallel, out _);
                        }

                        return state;
                    },
                    state =>
                    {
                        Interlocked.Add(ref totalLength, state.local);
                    });

                return totalLength;
            }

            private static int RecordChildrenRlpBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<BranchChildRlpMetadata> children, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    TrieNode? data = item.GetSlotRef(i);
                    if (data is null || ReferenceEquals(data, NullNode))
                    {
                        children[i] = new BranchChildRlpMetadata(BranchChildRlpKind.Empty, Rlp.OfEmptyByteArray.Length);
                        totalLength++;
                    }
                    else
                    {
                        totalLength += RecordResolvedBranchChild(tree, ref path, i, data, children, bufferPool, canBeParallel);
                    }
                }
                return totalLength;
            }

            private static int GetChildrenRlpLengthForBranchRlpParallel(ITrieNodeResolver tree, TreePath rootPath, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ParallelUnbalancedWork.For(0, BranchesCount, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                    (local: 0, item, tree, bufferPool, rootPath, canBeParallel),
                    static (i, state) =>
                    {
                        ValueRlpStream rlpStream = state.item.RlpStream;
                        state.item.SeekChild(ref rlpStream, i);
                        TrieNode? data = state.item.GetSlotRef(i);
                        if (data is null)
                        {
                            // Slot unresolved; canonical encoding lives in the parent RLP.
                            state.local += rlpStream.PeekNextRlpLength();
                        }
                        else if (ReferenceEquals(data, NullNode))
                        {
                            state.local++;
                        }
                        else
                        {
                            TreePath path = state.rootPath;
                            state.local += ResolveAndGetBranchChildRlpLength(state.tree, ref path, i, data, state.bufferPool, state.canBeParallel, out _);
                        }

                        return state;
                    },
                    state =>
                    {
                        Interlocked.Add(ref totalLength, state.local);
                    });

                return totalLength;
            }

            private static int RecordChildrenRlpBranchRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, CappedArray<byte> parentRlp, Span<BranchChildRlpMetadata> children, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ValueRlpStream rlpStream = new(parentRlp);
                rlpStream.Reset();
                rlpStream.SkipLength();
                for (int i = 0; i < BranchesCount; i++)
                {
                    int parentRlpOffset = rlpStream.Position;
                    int parentRlpLength = rlpStream.PeekNextRlpLength();
                    TrieNode? data = item.GetSlotRef(i);
                    if (data is null)
                    {
                        children[i] = new BranchChildRlpMetadata(BranchChildRlpKind.ParentRlpSlice, parentRlpLength, parentRlpOffset);
                        totalLength += parentRlpLength;
                    }
                    else if (ReferenceEquals(data, NullNode))
                    {
                        children[i] = new BranchChildRlpMetadata(BranchChildRlpKind.Empty, Rlp.OfEmptyByteArray.Length);
                        totalLength++;
                    }
                    else
                    {
                        totalLength += RecordResolvedBranchChild(tree, ref path, i, data, children, bufferPool, canBeParallel);
                    }

                    rlpStream.SkipBytes(parentRlpLength);
                }

                return totalLength;
            }

            private static int RecordResolvedBranchChild(
                ITrieNodeResolver tree,
                ref TreePath path,
                int childIndex,
                TrieNode data,
                Span<BranchChildRlpMetadata> children,
                ICappedArrayPool? bufferPool,
                bool canBeParallel)
            {
                int length = ResolveAndGetBranchChildRlpLength(tree, ref path, childIndex, data, bufferPool, canBeParallel, out bool hasKeccak);
                if (hasKeccak)
                {
                    children[childIndex] = new BranchChildRlpMetadata(BranchChildRlpKind.Keccak, Rlp.LengthOfKeccakRlp);
                    return Rlp.LengthOfKeccakRlp;
                }

                children[childIndex] = new BranchChildRlpMetadata(BranchChildRlpKind.Inline, length);
                return length;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int ResolveAndGetBranchChildRlpLength(ITrieNodeResolver tree, ref TreePath path, int childIndex, TrieNode data, ICappedArrayPool? bufferPool, bool canBeParallel, out bool hasKeccak)
            {
                ResolveBranchChildKey(tree, ref path, childIndex, data, bufferPool, canBeParallel);
                hasKeccak = data.HasFreshKeccak;
                return hasKeccak ? Rlp.LengthOfKeccakRlp : data.FullRlp.Length;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void ResolveBranchChildKey(ITrieNodeResolver tree, ref TreePath path, int childIndex, TrieNode data, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                path.AppendMut(childIndex);
                data.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                path.TruncateOne();
            }

            [SkipLocalsInit]
            private static void WriteRecordedChildrenRlpBranch(TrieNode item, ReadOnlySpan<byte> parentRlp, Span<BranchChildRlpMetadata> children, Span<byte> destination)
            {
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    BranchChildRlpMetadata child = children[i];
                    switch (child.Kind)
                    {
                        case BranchChildRlpKind.Empty:
                            destination[position++] = Rlp.EmptyByteArrayByte;
                            break;
                        case BranchChildRlpKind.ParentRlpSlice:
                            parentRlp.Slice(child.ParentRlpOffset, child.Length).CopyTo(destination.Slice(position, child.Length));
                            position += child.Length;
                            break;
                        case BranchChildRlpKind.Keccak:
                            {
                                TrieNode data = GetRecordedChild(item, i);
                                position = WriteResolvedBranchChildRlp(data, destination, position, requireKeccak: true);
                                break;
                            }
                        case BranchChildRlpKind.Inline:
                            {
                                TrieNode data = GetRecordedChild(item, i);
                                position = WriteResolvedBranchChildRlp(data, destination, position, child.Length);
                                break;
                            }
                    }
                }
                if (position != destination.Length)
                {
                    ThrowRecordedBranchLengthMismatch(destination.Length, position);
                }
            }

            private static TrieNode GetRecordedChild(TrieNode item, int childIndex)
            {
                TrieNode? data = item.GetSlotRef(childIndex);
                if (data is null || ReferenceEquals(data, NullNode))
                {
                    ThrowMissingRecordedChild();
                }

                return data;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int WriteResolvedBranchChildRlp(TrieNode data, Span<byte> destination, int position, int expectedInlineRlpLength = -1, bool requireKeccak = false)
            {
                if (data.TryGetKeccak(out ValueHash256 childKeccak))
                {
                    Debug.Assert(expectedInlineRlpLength < 0,
                        "Recorded inline branch child gained a keccak during RLP encoding.");
                    return Rlp.Encode(destination, position, in childKeccak);
                }

                if (requireKeccak)
                {
                    ThrowMissingRecordedKeccak();
                }

                Span<byte> fullRlp = data.FullRlp.AsSpan();
                if (expectedInlineRlpLength >= 0 && fullRlp.Length != expectedInlineRlpLength)
                {
                    ThrowChangedRecordedLength(expectedInlineRlpLength, fullRlp.Length);
                }

                fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                return position + fullRlp.Length;
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowMissingRecordedChild() =>
                throw new TrieException("Recorded branch child was removed during RLP encoding.");

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowMissingRecordedKeccak() =>
                throw new TrieException("Recorded branch child lost its keccak during RLP encoding.");

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowChangedRecordedLength(int expected, int actual) =>
                throw new TrieException($"Recorded branch child RLP length changed during encoding. Expected {expected}, actual {actual}.");

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowRecordedBranchLengthMismatch(int expected, int actual) =>
                throw new TrieException($"Recorded branch RLP length changed during encoding. Expected {expected}, actual {actual}.");

            private static void WriteChildrenRlpBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                // Tail call optimized.
                if (item.HasRlp)
                {
                    WriteChildrenRlpBranchRlp(tree, ref path, item, destination, bufferPool, canBeParallel);
                }
                else
                {
                    WriteChildrenRlpBranchNonRlp(tree, ref path, item, destination, bufferPool, canBeParallel);
                }
            }

            private static void WriteChildrenRlpBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    TrieNode? data = item.GetSlotRef(i);
                    if (data is null || ReferenceEquals(data, NullNode))
                    {
                        destination[position++] = Rlp.EmptyByteArrayByte;
                    }
                    else
                    {
                        ResolveBranchChildKey(tree, ref path, i, data, bufferPool, canBeParallel);
                        position = WriteResolvedBranchChildRlp(data, destination, position);
                    }
                }
            }

            private static void WriteChildrenRlpBranchRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                ValueRlpStream rlpStream = item.RlpStream;
                item.SeekChild(ref rlpStream, 0);
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    TrieNode? data = item.GetSlotRef(i);
                    if (data is null)
                    {
                        // Unresolved slot: copy the canonical bytes from the parent's RLP.
                        int length = rlpStream.PeekNextRlpLength();
                        ReadOnlySpan<byte> nextItem = rlpStream.Data.Slice(rlpStream.Position, length);
                        nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                        position += nextItem.Length;
                        rlpStream.SkipBytes(length);
                    }
                    else
                    {
                        if (ReferenceEquals(data, NullNode))
                        {
                            destination[position++] = Rlp.EmptyByteArrayByte;
                        }
                        else
                        {
                            ResolveBranchChildKey(tree, ref path, i, data, bufferPool, canBeParallel);
                            position = WriteResolvedBranchChildRlp(data, destination, position);
                        }

                        rlpStream.SkipItem();
                    }
                }
            }
        }
    }
}
