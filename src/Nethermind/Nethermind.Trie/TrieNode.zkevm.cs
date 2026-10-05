// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        /// <summary>Reads <c>_blockAndFlags</c> directly &mdash; see the std counterpart for the acquire read this replaces.</summary>
        /// <remarks>
        /// Single-threaded guest: nothing to publish, so this collapses to a plain field load and inlines
        /// into the flag properties, which are read per node touch.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadBlockAndFlags() => _blockAndFlags;

        /// <summary>Reads <c>_nodeData</c> directly &mdash; see the std counterpart for the acquire read this replaces.</summary>
        /// <remarks>Read by every node type test, several times per node touched on a trie walk.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private INodeData? ReadNodeData() => _nodeData;

        /// <summary>Whether this node is a leaf.</summary>
        /// <remarks>
        /// A type test rather than the std form's <see cref="NodeType"/> compare: the guest's whole-program
        /// compilation sees no class derived from <see cref="LeafData"/>, so this is one method-table
        /// compare instead of a dispatch over every node data class.
        /// </remarks>
        public bool IsLeaf => _nodeData is LeafData;

        /// <summary>Whether this node is an extension.</summary>
        /// <remarks><inheritdoc cref="IsLeaf" path="/remarks"/></remarks>
        public bool IsExtension => _nodeData is ExtensionData;

        // The node type, key, child slots and memory size go through INodeData here, as before. The std forms test
        // the sealed classes to avoid the JIT's dispatch stubs and cast cache, which the guest's whole-program
        // compilation doesn't have; in the guest the type tests cost more steps than the calls they replace.
        public NodeType NodeType => ReadNodeData()?.NodeType ?? NodeType.Unknown;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[]? ReadKey() => _nodeData is INodeWithKey node ? node?.Key : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref object? DataItem(int i) => ref _nodeData![i];

        public long GetMemorySize(bool recursive)
        {
            int keccakSize = Keccak is null ? MemorySizes.RefSize : MemorySizes.RefSize + Hash256.MemorySize;
            CappedArray<byte> rlp = ReadRlp();
            long rlpSize = MemorySizes.RefSize + (rlp.IsNotNull ? MemorySizes.ArrayOverhead + rlp.UnderlyingLength : 0);
            long dataSize = MemorySizes.RefSize + (_nodeData?.MemorySize ?? 0);
            int objectOverhead = MemorySizes.ObjectHeaderMethodTable;
            int blockAndFlagsSize = sizeof(long);

            if (_nodeData is BranchData data)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    object? child = data[i];
                    dataSize += child switch
                    {
                        null => 0,
                        Hash256 => Hash256.MemorySize,
                        byte[] array => MemorySizes.ArrayOverhead + array.Length,
                        CappedArray<byte> cappedArray => MemorySizes.ArrayOverhead + cappedArray.UnderlyingLength +
                                                         MemorySizes.SmallObjectOverhead,
                        _ => recursive && child is TrieNode node ? node.GetMemorySize(true) : 0
                    };
                }
            }
            else if (_nodeData is ExtensionData extensionData)
            {
                dataSize += extensionData.Value switch
                {
                    null => 0,
                    Hash256 => Hash256.MemorySize,
                    byte[] array => MemorySizes.ArrayOverhead + array.Length,
                    CappedArray<byte> cappedArray => MemorySizes.ArrayOverhead + cappedArray.UnderlyingLength +
                                                     MemorySizes.SmallObjectOverhead,
                    _ => recursive && extensionData.Value is TrieNode node ? node.GetMemorySize(true) : 0
                };
            }

            long unaligned = keccakSize +
                             rlpSize +
                             dataSize +
                             blockAndFlagsSize +
                             objectOverhead;

            return MemorySizes.Align(unaligned);
        }

        /// <summary>Stores <c>_blockAndFlags</c> and reports the exchange as having succeeded.</summary>
        /// <remarks>
        /// Keeps the shape of the compare-and-exchange it replaces &mdash; returning
        /// <paramref name="comparand"/> makes each caller's retry loop exit after one pass &mdash; but
        /// ignores it, so the store is unconditional. Callers must therefore read the field immediately
        /// before exchanging: no writer can race them in the guest, but a stale
        /// <paramref name="comparand"/> would be overwritten here rather than retried.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ExchangeBlockAndFlags(byte newValue, byte comparand)
        {
            _blockAndFlags = newValue;
            return comparand;
        }

        /// <summary>Reads <c>_rlpArray</c> for a presence check &mdash; see the std counterpart for the acquire read this replaces.</summary>
        /// <remarks>Read per child while encoding a branch, so the fence it drops is paid sixteen times per node.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[]? ReadRlpArray() => _rlpArray;

        /// <summary>
        /// Read _rlp directly &mdash; see the std counterpart for the seqlock this replaces.
        /// </summary>
        /// <remarks>
        /// No writer can race a reader in the guest, so the seqlock collapses to two loads. Being
        /// this small also lets it inline into the several call sites that read the RLP per node.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private CappedArray<byte> ReadRlp()
        {
            byte[]? array = _rlpArray;
            return array is null ? default : new CappedArray<byte>(array, (int)(uint)_rlpSeqAndLength);
        }

        /// <summary>
        /// Write _rlp directly &mdash; see the std counterpart for the seqlock this replaces.
        /// </summary>
        /// <remarks>
        /// No competing writer in the guest, so the publish is just the two field stores.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteRlp(CappedArray<byte> value) => InitRlp(value);

        /// <summary>Loads, if it has none, and decodes the RLP of this node of unknown type.</summary>
        /// <remarks>
        /// Without the std form's wrapping of a decoding error into a <see cref="TrieNodeException"/>: the guest fails the
        /// block on any exception, and the handler costs every resolve a frame pointer and spilled arguments.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ResolveUnknownNodeWithContext(ITrieNodeResolver tree, in TreePath path, ReadFlags readFlags,
            ICappedArrayPool? bufferPool) => ResolveUnknownNode(tree, path, readFlags, bufferPool);

        /// <inheritdoc cref="Unseal" path="/summary"/>
        /// <remarks>This node itself, left as a clone would be: dirty, unhashed, and keeping its RLP and data. The guest's
        /// raw trie store builds a fresh node for every lookup and caches none, so no other trie or block can hold this
        /// one, and every ancestor on the path being written is unsealed or replaced too, so none keeps the old hash.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TrieNode Unseal()
        {
            Keccak = null;
            _blockAndFlags = _dirtyMask;
            return this;
        }

        /// <inheritdoc cref="PruneTraversedChildren"/>
        /// <remarks>The guest verifies one block and exits, so there is no cache to keep small and nothing
        /// to amortise a re-resolve against: dropping a child only guarantees decoding its RLP again the
        /// next time the path is walked. Over a mainnet block the witness holds 26,025 distinct nodes while
        /// the trie decodes 34,789 times, so 8,764 of those decodes are repeats.</remarks>
        private const bool PruneTraversedChildren = false;

        /// <summary>How <see cref="GetChildWithChildPath"/> is inlined: always.</summary>
        /// <remarks><see cref="PatriciaTree"/>'s set walk outgrows the inliner's budget and
        /// would otherwise call it out of line per level, a frame and five saved registers for a slot load and a type test.</remarks>
        private const MethodImplOptions GetChildWithChildPathInlining = MethodImplOptions.AggressiveInlining;
    }
}
