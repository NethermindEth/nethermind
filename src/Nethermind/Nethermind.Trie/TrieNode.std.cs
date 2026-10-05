// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadBlockAndFlags() => Volatile.Read(ref _blockAndFlags);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private INodeData? ReadNodeData() => Volatile.Read(ref _nodeData);

        // Type tests, not NodeType compares: the node data classes are sealed, so each is one method-table compare.
        public bool IsLeaf => ReadNodeData() is LeafData;

        public bool IsExtension => ReadNodeData() is ExtensionData;

        // Acquire pairs with the release publication of _nodeData in DecodeRlp: a concurrent resolver may publish a
        // decode while another thread tests whether the node is resolved, and the decoded fields must be visible with it.
        // A type switch over the sealed node data classes: an INodeData call here would go through interface dispatch,
        // and trie walks read the node type several times per node.
        public NodeType NodeType => ReadNodeData() switch
        {
            BranchData => NodeType.Branch,
            ExtensionData => NodeType.Extension,
            LeafData => NodeType.Leaf,
            null => NodeType.Unknown,
            { } other => other.NodeType,
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[]? ReadKey() => _nodeData switch
        {
            ExtensionData extension => extension.Key,
            LeafData leaf => leaf.Key,
            INodeWithKey node => node.Key,
            _ => null,
        };

        /// <summary>Child slot <paramref name="i"/> of the node data, reached through the sealed classes, not <see cref="INodeData"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref object? DataItem(int i)
        {
            INodeData? nodeData = _nodeData;
            if (nodeData is BranchData branch) return ref branch[i];
            if (nodeData is ExtensionData extension) return ref extension[i];
            return ref nodeData![i];
        }

        public long GetMemorySize(bool recursive)
        {
            int keccakSize = Keccak is null ? MemorySizes.RefSize : MemorySizes.RefSize + Hash256.MemorySize;
            CappedArray<byte> rlp = ReadRlp();
            long rlpSize = MemorySizes.RefSize + (rlp.IsNotNull ? MemorySizes.ArrayOverhead + rlp.UnderlyingLength : 0);
            // A switch over the sealed node data types rather than an interface call: the trie node cache sizes every
            // node it takes in.
            long dataSize = MemorySizes.RefSize + _nodeData switch
            {
                BranchData branch => branch.MemorySize,
                ExtensionData extension => extension.MemorySize,
                LeafData leaf => leaf.MemorySize,
                null => 0,
                _ => _nodeData.MemorySize,
            };
            int objectOverhead = MemorySizes.ObjectHeaderMethodTable;
            int blockAndFlagsSize = sizeof(long);

            if (_nodeData is BranchData data)
            {
                for (int i = 0; i < data.Length; i++) dataSize += ChildMemorySize(data[i], recursive);
            }
            else if (_nodeData is ExtensionData extensionData)
            {
                dataSize += ChildMemorySize(extensionData.Value, recursive);
            }

            long unaligned = keccakSize +
                             rlpSize +
                             dataSize +
                             blockAndFlagsSize +
                             objectOverhead;

            return MemorySizes.Align(unaligned);
        }

        // Exact and sealed type tests only: `is byte[]` also admits covariant arrays, so the runtime answers it through
        // its shared cast cache for every child that is not an array, at a cost that changed from process to process.
        private static long ChildMemorySize(object? child, bool recursive) => child switch
        {
            null => 0,
            TrieNode node => recursive ? node.GetMemorySize(true) : 0,
            Hash256 => Hash256.MemorySize,
            CappedArray<byte> cappedArray => MemorySizes.ArrayOverhead + cappedArray.UnderlyingLength + MemorySizes.SmallObjectOverhead,
            _ when child.GetType() == typeof(byte[]) => MemorySizes.ArrayOverhead + Unsafe.As<byte[]>(child).Length,
            _ => 0,
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ExchangeBlockAndFlags(byte newValue, byte comparand)
            => Interlocked.CompareExchange(ref _blockAndFlags, newValue, comparand);

        /// <summary>Reads <c>_rlpArray</c> for a presence check, without the seqlock <see cref="ReadRlp"/> needs.</summary>
        /// <remarks>Only the reference is read, so a torn length cannot be observed and an acquire read suffices.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[]? ReadRlpArray() => Volatile.Read(ref _rlpArray);

        /// <summary>
        /// Atomically read _rlp using seqlock: retry if a concurrent write is detected.
        /// Memory barriers ensure ARM64 correctness (matching SeqlockCache/KeccakCache patterns).
        /// </summary>
        private CappedArray<byte> ReadRlp()
        {
            SpinWait spin = default;
            ulong seqBefore, seqAfter;
            byte[]? array;
            while (true)
            {
                seqBefore = Volatile.Read(ref _rlpSeqAndLength);
                if ((seqBefore >> 32 & 1) != 0) { spin.SpinOnce(); continue; }
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();
                array = _rlpArray;
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();
                seqAfter = Volatile.Read(ref _rlpSeqAndLength);
                if (seqBefore == seqAfter) break;
                spin.SpinOnce();
            }

            return array is null ? default : new CappedArray<byte>(array, (int)(seqBefore & 0xFFFFFFFF));
        }

        /// <summary>
        /// Atomically write _rlp using seqlock: odd sequence signals write-in-progress.
        /// CAS on even sequences only — if another writer is active (odd), spin until it completes.
        /// Last writer wins: all writers write the same resolved data for a given node.
        /// Sequence uses bits 1-31 (31 bits, ~2 billion writes before wrap); bit 0 is the lock flag.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // CAS dominates latency; avoid code bloat at 5+ call sites
        internal void WriteRlp(CappedArray<byte> value)
        {
            SpinWait spin = default;
            while (true)
            {
                ulong current = Volatile.Read(ref _rlpSeqAndLength);
                ulong seq = current >> 32;
                if ((seq & 1) != 0)
                {
                    // Another writer is active — spin until it completes
                    spin.SpinOnce();
                    continue;
                }
                // Set lock bit (odd) — seq | 1 is always odd regardless of overflow
                ulong writing = (seq | 1) << 32;
                if (Interlocked.CompareExchange(ref _rlpSeqAndLength, writing, current) == current)
                {
                    Volatile.Write(ref _rlpArray, value.UnderlyingArray);
                    // Advance sequence by 2 and clear lock bit (even), store final length
                    ulong doneSeq = (seq + 2) & 0xFFFFFFFE;
                    Volatile.Write(ref _rlpSeqAndLength, doneSeq << 32 | (uint)value.Length);
                    return;
                }
                spin.SpinOnce(); // CAS failed — another writer raced; back off before retry
            }
        }

        /// <summary>Publishes the RLP a node was resolved from.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteLoadedRlp(CappedArray<byte> value) => WriteRlp(value);

        /// <summary>Gives this clone of <paramref name="original"/> its RLP, <paramref name="rlp"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitClonedRlp(CappedArray<byte> rlp, TrieNode original) => InitRlp(rlp);

        /// <summary>What <see cref="ComputeKeccak"/> may reuse of a node's RLP from before it is re-encoded: nothing here.</summary>
        private readonly struct PreviousRlp { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private PreviousRlp ReadPreviousRlp() => default;

        /// <summary>Computes the keccak of <paramref name="rlp"/>, this node's encoding.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Hash256 ComputeKeccak(ReadOnlySpan<byte> rlp, in PreviousRlp previous) =>
            Nethermind.Core.Crypto.Keccak.Compute(rlp);

        /// <summary>Whether a resolved, persisted child is dropped back to its hash once traversed.</summary>
        /// <remarks>Worth it for a long-lived process, whose node cache would otherwise retain every deep
        /// persisted path it has ever walked. See <c>TrieNode.zkevm.cs</c> for why the guest declines.</remarks>
        private const bool PruneTraversedChildren = true;
    }
}
