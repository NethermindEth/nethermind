// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
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

        // The node type, key and memory size go through INodeData here, as before. The std forms test
        // the sealed classes to avoid the JIT's dispatch stubs and cast cache, which the guest's whole-program
        // compilation doesn't have; in the guest the type tests cost more steps than the calls they replace.
        public NodeType NodeType => ReadNodeData()?.NodeType ?? NodeType.Unknown;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[]? ReadKey() => _nodeData is INodeWithKey node ? node?.Key : null;

        /// <summary>Child slot <paramref name="i"/> of the node data, a branch's reached without <see cref="INodeData"/>.</summary>
        /// <remarks>ILC devirtualizes the interface indexer behind two type tests but still calls it out of line, once per
        /// level of every trie write. One test for the branch, the type nearly every slot write goes to, inlines its
        /// slot; a second for the extension measured slower than leaving the rest to the indexer.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref object? DataItem(int i)
        {
            INodeData? nodeData = _nodeData;
            if (nodeData is BranchData branch) return ref branch[i];
            return ref nodeData![i];
        }

        /// <summary>Whether this node awaits its hash, dirty, with <paramref name="child"/>, also unhashed, in slot <paramref name="i"/>.</summary>
        /// <remarks>Such a node gains nothing from <see cref="SetChild"/> with that child: the slot and hash stay as they are.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsPendingWith(int i, TrieNode child) =>
            Keccak is null && child.Keccak is null && IsDirty && ReferenceEquals(DataItem(i), child);

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

        /// <summary>Publishes the RLP a node was resolved from, tagged with the witness node the store handed out last.</summary>
        /// <remarks>
        /// The tag rides in the length word's upper half, which the guest has no seqlock sequence for, so any later
        /// <see cref="WriteRlp"/> drops it with the RLP it names. A tag the store did not set for this load is stale, which
        /// <see cref="KeccakHash.ComputeHash256OfEdited"/> detects: it resumes only from the very array the tag names.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteLoadedRlp(CappedArray<byte> value)
        {
            _rlpArray = value.UnderlyingArray;
            _rlpSeqAndLength = (uint)value.Length | (ulong)KeccakHash.LoadedWitnessNode() << 32;
        }

        /// <summary>Gives this clone of <paramref name="original"/> its RLP, <paramref name="rlp"/>, with the original's witness tag.</summary>
        /// <remarks>Without the tag a cloned branch could not resume from the witness node it was loaded from. A trie write
        /// unseals the nodes on its path in place instead, which keeps the tag; see <see cref="Unseal"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitClonedRlp(CappedArray<byte> rlp, TrieNode original)
        {
            _rlpArray = rlp.UnderlyingArray;
            _rlpSeqAndLength = original._rlpSeqAndLength;
        }

        /// <summary>A node's RLP from before it is re-encoded, with its witness tag, for <see cref="ComputeKeccak"/>.</summary>
        private readonly struct PreviousRlp(byte[]? array, nint tag)
        {
            public readonly byte[]? Array = array;
            public readonly nint Tag = tag;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private PreviousRlp ReadPreviousRlp() => new(_rlpArray, (nint)(_rlpSeqAndLength >> 32));

        /// <summary>Computes the keccak of <paramref name="rlp"/>, this node's encoding.</summary>
        /// <remarks>A branch re-encoded from a witness node resumes from the sponge state of its unchanged leading
        /// rate blocks; see <see cref="KeccakHash.ComputeHash256OfEdited"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Hash256 ComputeKeccak(ReadOnlySpan<byte> rlp, in PreviousRlp previous) =>
            KeccakHash.IsResumable(rlp.Length)
                ? new Hash256(KeccakHash.ComputeHash256OfEdited(rlp, previous.Array, previous.Tag))
                : Nethermind.Core.Crypto.Keccak.Compute(rlp);

        /// <summary>Loads, if it has none, and decodes the RLP of this node of unknown type.</summary>
        /// <remarks>
        /// Without the std form's wrapping of a decoding error into a <see cref="TrieNodeException"/>: the guest fails the
        /// block on any exception, and the handler costs every resolve a frame pointer and spilled arguments.
        /// Inlined into each walk, unlike the std form: nearly every witness node is resolved exactly once, so the
        /// call and its saved registers were paid per node.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        /// <summary>Whether a re-encoded branch keeps its previous RLP's value as it stands, rather than walking to it to drop it.</summary>
        /// <remarks>Every branch RLP in the guest comes from the witness, under the parent block's state root, or from this
        /// encoder, and neither holds a value: state and storage keys are all 32 bytes. So the value is the last byte, 0x80,
        /// and copying it spares the walk over the slots past a branch's last changed child.</remarks>
        private static bool CopiesBranchValue => true;

        /// <summary>Whether a branch hashed as soon as it is encoded keeps the RLP it had rather than its new one.</summary>
        /// <remarks>
        /// The guest never commits a trie, so a new branch RLP has no reader once hashed: a parent takes the hash, and a
        /// write that reaches the branch again unseals it and re-encodes it from its slots. The RLP kept, from the witness
        /// or none for a branch built in this block, still encodes every null slot, as a slot is only ever filled. So the
        /// new RLP is written to scratch from <see cref="RentBranchRlp"/> and dropped, which spares the memory a stored copy
        /// per encoded branch would first-touch. A branch short enough to be embedded is kept as before: its parent copies it.
        /// Only where <see cref="FirstTouchMemoryFlag"/> is on: the other guests pay more for the scratch bookkeeping than they save.
        /// </remarks>
        private static bool KeepsRlpOfHashedBranch => FirstTouchMemoryFlag.IsActive;

        /// <summary>The scratch branches are encoded into; see <see cref="KeepsRlpOfHashedBranch"/>.</summary>
        /// <remarks>A class of its own, with no initializer, so each access is a plain load rather than a class-initialization check.</remarks>
        private static class BranchRlpScratch
        {
            /// <summary>Per depth, the scratch a branch at that depth is encoded into.</summary>
            /// <remarks>A branch's children are all deeper, so one is hashed before any other branch at its depth is encoded.
            /// Sixty-four depths: a branch has a child one nibble deeper, and a path is at most sixty-four nibbles.</remarks>
            public static byte[]?[]? ByDepth;
        }

        /// <summary>Rents the buffer the encoding of the branch at <paramref name="path"/> is written into: where
        /// <see cref="KeepsRlpOfHashedBranch"/>, the scratch for its depth, else from <paramref name="pool"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static CappedArray<byte> RentBranchRlp(ICappedArrayPool? pool, int length, in TreePath path) =>
            KeepsRlpOfHashedBranch ? RentBranchRlpScratch(length, path) : pool.SafeRent(length);

        /// <summary>Rents the scratch for the depth of <paramref name="path"/>, holding at least <paramref name="length"/> bytes.</summary>
        /// <remarks>Valid until the next branch at that depth is encoded; <see cref="StorableBranchRlp"/> copies out what a node keeps.</remarks>
        private static CappedArray<byte> RentBranchRlpScratch(int length, in TreePath path)
        {
            // Read by value: a reference to an element of an array of references costs a covariance check call.
            byte[]?[] byDepth = BranchRlpScratch.ByDepth ??= new byte[2 * Hash256.Size][];
            byte[]? scratch = byDepth[path.Length];
            if (scratch is null || scratch.Length < length) byDepth[path.Length] = scratch = new byte[Math.Max(length, FullBranchRlpLength)];
            return new CappedArray<byte>(scratch, length);
        }

        /// <summary>Returns <paramref name="rlp"/>, a branch encoding, in a buffer the node can keep: a copy out of the scratch.</summary>
        private static CappedArray<byte> StorableBranchRlp(CappedArray<byte> rlp, ICappedArrayPool? pool)
        {
            CappedArray<byte> copy = pool.SafeRent(rlp.Length);
            rlp.AsSpan().CopyTo(copy.AsSpan());
            return copy;
        }

        /// <summary>How <see cref="GetChildWithChildPath"/> is inlined: always.</summary>
        /// <remarks><see cref="PatriciaTree"/>'s set walk outgrows the inliner's budget and
        /// would otherwise call it out of line per level, a frame and five saved registers for a slot load and a type test.</remarks>
        private const MethodImplOptions GetChildWithChildPathInlining = MethodImplOptions.AggressiveInlining;

        /// <summary>How <see cref="ResolveChildFromRlp"/> is inlined: always.</summary>
        /// <remarks>Nearly every witness node is a child resolved once, and the call spilled the walk's live registers around it.</remarks>
        private const MethodImplOptions ResolveChildFromRlpInlining = MethodImplOptions.AggressiveInlining;

        /// <summary>How <see cref="SetChild"/> is inlined: always.</summary>
        /// <remarks>Called per level as a trie write climbs back to the root, where an out-of-line call costs more than the
        /// sealed check and slot store it makes.</remarks>
        private const MethodImplOptions SetChildInlining = MethodImplOptions.AggressiveInlining;

        /// <summary>How <see cref="PrepareRlp"/> is inlined: always.</summary>
        /// <remarks>Entered once per node a root computation encodes, from the key resolution of its parent's encoder,
        /// where an out-of-line call spills seven registers to test a flag and pick an encoder.</remarks>
        private const MethodImplOptions PrepareRlpInlining = MethodImplOptions.AggressiveInlining;

        /// <summary>Drops a leaf's RLP once its key has been replaced, so the RLP left on a leaf always carries the leaf's key.</summary>
        /// <remarks>
        /// What <see cref="TryEncodeLeafWithStoredKey"/> relies on. A leaf holds its value in its data, so nothing else reads
        /// its RLP; an extension keeps it, as its child may still have to be resolved from it.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnKeyChanged()
        {
            if (_nodeData is LeafData) _rlpArray = null;
        }

        /// <summary>Encodes <paramref name="node"/>, a leaf, by reusing the key item of its previous RLP.</summary>
        /// <remarks>
        /// A leaf re-encoded in a commit has nearly always kept its key and changed only its value, and packing the key's
        /// nibbles back into its hex prefix is most of what encoding it costs. <see cref="OnKeyChanged"/> drops the RLP
        /// of a leaf whose key changes, so any RLP still present carries the current key.
        /// </remarks>
        /// <returns>Whether the leaf had an RLP, or else a hex-prefix key, to reuse; when not, <paramref name="rlp"/> is not set.</returns>
        private static bool TryEncodeLeafWithStoredKey(TrieNode node, ICappedArrayPool? pool, out CappedArray<byte> rlp)
        {
            CappedArray<byte> previous = node.ReadRlp();
            ReadOnlySpan<byte> previousRlp = previous.AsSpan();
            if (previousRlp.IsEmpty) return TryEncodeLeafWithCompactKey(node, pool, out rlp);

            int keyStart = previousRlp[0] < 0xf8 ? 1 : 1 + previousRlp[0] - 0xf7;
            // A hex-prefix key is one to thirty-three bytes: a single byte below 0x80, or a short string.
            int keyPrefix = previousRlp[keyStart];
            int keyItemLength = keyPrefix < 0x80 ? 1 : 1 + keyPrefix - 0x80;
            ReadOnlySpan<byte> keyItem = previousRlp.Slice(keyStart, keyItemLength);
            ReadOnlySpan<byte> value = node.Value.AsSpan();

            int contentLength = keyItemLength + Rlp.LengthOf(value);
            rlp = pool.SafeRent(Rlp.LengthOfSequence(contentLength));
            Span<byte> destination = rlp.AsSpan();
            int position = Rlp.StartSequence(destination, 0, contentLength);
            keyItem.CopyTo(destination[position..]);
            Rlp.Encode(destination, position + keyItemLength, value);
            return true;
        }

        /// <summary>Encodes <paramref name="node"/>, a leaf without RLP, from the hex-prefix key its data keeps, as a leaf
        /// built by a raw-key write does.</summary>
        /// <returns>Whether the leaf kept such a key; when not, <paramref name="rlp"/> is not set.</returns>
        private static bool TryEncodeLeafWithCompactKey(TrieNode node, ICappedArrayPool? pool, out CappedArray<byte> rlp)
        {
            if (node._nodeData is not LeafData leaf || !leaf.TryGetCompactKey(out ReadOnlySpan<byte> compactKey))
            {
                rlp = default;
                return false;
            }

            ReadOnlySpan<byte> value = leaf.Value.AsSpan();
            int contentLength = Rlp.LengthOf(compactKey) + Rlp.LengthOf(value);
            rlp = pool.SafeRent(Rlp.LengthOfSequence(contentLength));
            Span<byte> destination = rlp.AsSpan();
            int position = Rlp.StartSequence(destination, 0, contentLength);
            position = Rlp.Encode(destination, position, compactKey);
            Rlp.Encode(destination, position, value);
            return true;
        }

        /// <summary>Resolves child <paramref name="i"/> from an RLP other than a full branch's, most often a branch with empty slots.</summary>
        /// <remarks>
        /// Skips the slots before the child knowing only the two item shapes such a branch holds, an empty slot and a hash,
        /// and hands any other shape, an extension or a node without RLP to <see cref="ResolveChildFromAnyRlp"/>. Every
        /// call it makes is a tail call, so unlike that general item scan it saves no registers.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private object? ResolveChildFromSparseRlp(ITrieNodeResolver tree, ref TreePath childPath, ref object? data, int i, CappedArray<byte> rlp)
        {
            ReadOnlySpan<byte> span = rlp.AsSpan();
            if (span.Length == 0 || span[0] < 0xc0 || _nodeData is not BranchData)
                return ResolveChildFromAnyRlp(tree, ref childPath, ref data, i, rlp);

            int position = span[0] < 0xf8 ? 1 : 1 + span[0] - 0xf7;
            for (int skipped = 0; skipped < i; skipped++)
            {
                int prefix = span[position];
                if (prefix == EmptyItem) position++;
                else if (prefix == HashItem) position += 1 + Hash256.Size;
                else return ResolveChildFromAnyRlp(tree, ref childPath, ref data, i, rlp);
            }

            switch (span[position])
            {
                case EmptyItem:
                    data = _nullNode;
                    return _nullNode;
                case HashItem:
                    return ResolveHashedChild(tree, ref childPath, ref data, span.Slice(position + 1, Hash256.Size));
                default:
                    return ResolveChildFromAnyRlp(tree, ref childPath, ref data, i, rlp);
            }
        }

        private const int EmptyItem = 0x80;
        private const int HashItem = 0x80 + Hash256.Size;

        /// <summary>Creates the data of a leaf decoded from this node's RLP, with the hex-prefix key <paramref name="compactKey"/>
        /// ending at <paramref name="compactKeyEnd"/>.</summary>
        /// <remarks>The key stays in the RLP until read: an RLP array is never written once a node holds it here, as the
        /// guest's buffer pool hands out fresh arrays and takes none back.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private LeafData CreateLeafData(ReadOnlySpan<byte> compactKey, int compactKeyEnd, CappedArray<byte> value) =>
            new(_rlpArray!, compactKeyEnd - compactKey.Length, compactKey.Length, value);

        /// <summary>Deepens <paramref name="path"/> by one level for a child of the node being encoded, leaving its nibbles alone.</summary>
        /// <remarks>
        /// An encoder reads the path only for its depth, which tells the root, hashed whatever its length, from a child
        /// short enough to embed. Its nibbles would only reach the node store, which in the guest is keyed by hash alone
        /// (see <c>WitnessNodeStorage.zkevm.cs</c>), the reason <see cref="PatriciaTree"/> already leaves them untracked.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnterChildPath(ref TreePath path, int index) => path.AppendDepth();

        /// <summary>Undoes <see cref="EnterChildPath"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LeaveChildPath(ref TreePath path) => path.TruncateDepth();
    }
}
