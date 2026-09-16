// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

using MemoryMarshal = System.Runtime.InteropServices.MemoryMarshal;

namespace Nethermind.State.Proofs;

// The indexed tries from Yellow Paper section 4.4 have prefix-free RLP integer keys,
// ordered 1..127, 0, 128..N, so each subtree can be hashed before encoding its sibling.
internal static class IndexedTrieRoot
{
    internal const int LeafBatchSize = 16;
    internal const int MinItemsForParallelRootHash = 64;
    internal const int MinReceiptsForParallelRootHash = LeafBatchSize;

    internal interface IValueEncoder<T>
    {
        ReadOnlySpan<byte> GetEncodedValue(T item);
        int GetLength(T item);
        void Encode<TWriter>(ref TWriter writer, T item) where TWriter : struct, IRlpWriteBackend, allows ref struct;
    }

    /// <summary>Provides 1440 bytes for eight padded rate blocks, hashes, indices, lengths, and an encoded path.</summary>
    [InlineArray(45)]
    private struct LeafBatchBuffer
    {
        private Vector256<byte> _element0;
    }

    /// <summary>Provides 608 bytes for sixteen leaf descriptors and eight output hashes, positions, and lengths.</summary>
    [InlineArray(19)]
    private struct MultiBlockLeafBuffer
    {
        private Vector256<byte> _element0;
    }
    [InlineArray(142)]
    private struct BranchBatchBuffer
    {
        private Vector256<byte> _element0;
    }

    internal readonly ref struct Calculator<T, TEncoder>(ReadOnlySpan<T> items, TEncoder encoder,
        ReadOnlySpan<NodeReference> leaves = default) where TEncoder : struct, IValueEncoder<T>
    {
        private const int BranchPrefixLength = 3;
        private const int PrecomputedBranchLength = -Keccak.Size;
        private const int BranchBatchSize = 8;
        private const int BranchChildCount = 16;
        private const int FullBranchLength = BranchPrefixLength + MaxBranchContentLength;
        private const int MaxBranchContentLength = 16 * Rlp.LengthOfKeccakRlp + 1;
        // A 136-byte Keccak rate block leaves 11 bytes for the longest RLP/path prefix and one for padding.
        private const int MaxSingleBlockValueLength = 124;
        private const int MaxMultiBlockValueLength = 16 * 136 - 12;
        private readonly ReadOnlySpan<T> _items = items;
        private readonly ReadOnlySpan<NodeReference> _leaves = leaves;

        public Hash256 Calculate(bool canBeParallel = true, int minItemsForParallel = MinItemsForParallelRootHash)
            => _items.IsEmpty ? Keccak.EmptyTreeHash
                : Avx512F.IsSupported && _leaves.IsEmpty && _items.Length is >= 8 and <= MinItemsForParallelRootHash
                    && CanBatchMultiBlockLeaves(_items[GetIndex(0)]) ? CalculateMultiBlockSequential()
                // One batch is the floor: below it there is no fan-out, and a lone leaf gets the wrong depth.
                : !canBeParallel || RuntimeInformation.IsSingleProcessor || _items.Length <= Math.Max(LeafBatchSize, minItemsForParallel)
                ? CalculateSequential()
                : CalculateParallel();

        [SkipLocalsInit]
        private void BatchTerminalBranches(Span<NodeReference> references)
        {
            Unsafe.SkipInit(out BranchBatchBuffer scratch);
            Span<byte> memory = MemoryMarshal.AsBytes((Span<Vector256<byte>>)scratch);
            Span<byte> blocks = memory[..(BranchBatchSize * FullBranchLength)];
            Span<byte> hashes = memory.Slice(blocks.Length, BranchBatchSize * Keccak.Size);
            Span<int> positions = MemoryMarshal.Cast<byte, int>(memory.Slice(blocks.Length + hashes.Length, BranchBatchSize * sizeof(int)));
            int pending = 0;
            for (int start = 0; start <= references.Length - (BranchBatchSize - pending) * BranchChildCount; start++)
            {
                // Sixteen aligned consecutive indices share every RLP key nibble except the last.
                int index = GetIndex(start);
                if ((index & 15) != 0 || GetIndex(start + BranchChildCount - 1) != index + BranchChildCount - 1) continue;
                bool allHashed = true;
                for (int j = 0; j < BranchChildCount; j++)
                {
                    if (references[start + j].Length != Keccak.Size)
                    {
                        allHashed = false;
                        break;
                    }
                }
                if (!allHashed) continue;
                Span<byte> block = blocks.Slice(pending * FullBranchLength, FullBranchLength);
                Rlp.StartSequence(block, 0, MaxBranchContentLength);
                for (int j = 0; j < BranchChildCount; j++)
                {
                    int offset = BranchPrefixLength + j * Rlp.LengthOfKeccakRlp;
                    block[offset] = 0xa0;
                    references[start + j].Value.Bytes.CopyTo(block.Slice(offset + 1, Keccak.Size));
                }
                block[FullBranchLength - 1] = Rlp.EmptyByteArrayByte;
                positions[pending++] = start;
                start += BranchChildCount - 1;
                if (pending != BranchBatchSize) continue;
                KeccakHash.ComputeHash532Bytes8Avx512(ref MemoryMarshal.GetReference(blocks), ref MemoryMarshal.GetReference(hashes));
                for (int j = 0; j < BranchBatchSize; j++)
                {
                    ValueHash256 hash = default;
                    hashes.Slice(j * Keccak.Size, Keccak.Size).CopyTo(hash.BytesAsSpan);
                    references[positions[j]] = new NodeReference(hash, PrecomputedBranchLength);
                }
                pending = 0;
            }
        }

        private bool CanBatchLeavesSequentially()
        {
            if (typeof(T) == typeof(Withdrawal)) return _items.Length >= 8;
            if (_items.Length is < LeafBatchSize or > MinItemsForParallelRootHash)
                return false;
            foreach (T item in _items)
                if (encoder.GetEncodedValue(item).Length is < Keccak.Size or > MaxSingleBlockValueLength)
                    return false;
            return true;
        }

        private Hash256 CalculateEncodedSequential()
        {
            using ArrayPoolList<NodeReference> references = new(_items.Length, _items.Length);
            CalculateEncodedLeafBatch(0, _items.Length, references.AsSpan());
            return new Calculator<T, TEncoder>(_items, encoder, references.AsSpan()).CalculateSequential();
        }

        private bool CanBatchMultiBlockLeaves(T item)
            => (typeof(T) == typeof(TxReceipt) ? encoder.GetLength(item)
                : typeof(T) == typeof(byte[]) || typeof(T) == typeof(ReadOnlyMemory<byte>) ? encoder.GetEncodedValue(item).Length : 0)
                is > MaxSingleBlockValueLength and <= MaxMultiBlockValueLength;

        private Hash256 CalculateMultiBlockSequential()
        {
            using ArrayPoolList<NodeReference> references = new(_items.Length, _items.Length);
            for (int start = 0; start < _items.Length; start += LeafBatchSize)
                CalculateMultiBlockLeafBatch(start, Math.Min(start + LeafBatchSize, _items.Length), references.AsSpan());
            return new Calculator<T, TEncoder>(_items, encoder, references.AsSpan()).CalculateSequential();
        }

        private Hash256 CalculateParallel()
        {
            Debug.Assert(_items.Length > 1);
            using ArrayPoolList<T> inputs = new(_items);
            using ArrayPoolList<NodeReference> references = new(_items.Length, _items.Length);
            TEncoder leafEncoder = encoder;
            try
            {
                Parallel.For(0, (_items.Length - 1) / LeafBatchSize + 1, RuntimeInformation.ParallelOptionsLogicalCores, batch =>
                {
                    Calculator<T, TEncoder> calculator = new(inputs.AsSpan(), leafEncoder);
                    int start = batch * LeafBatchSize;
                    int end = start + Math.Min(LeafBatchSize, inputs.Count - start);
                    if (Avx512F.IsSupported && typeof(T) == typeof(TxReceipt)
                        && leafEncoder.GetLength(inputs[calculator.GetIndex(start)]) is > MaxSingleBlockValueLength and <= MaxMultiBlockValueLength)
                    {
                        calculator.CalculateMultiBlockLeafBatch(start, end, references.AsSpan());
                        return;
                    }
                    if (Avx2.IsSupported && (typeof(T) == typeof(byte[]) || typeof(T) == typeof(ReadOnlyMemory<byte>)))
                    {
                        int valueLength = leafEncoder.GetEncodedValue(inputs[calculator.GetIndex(start)]).Length;
                        if (valueLength is >= Keccak.Size and <= MaxSingleBlockValueLength)
                        {
                            calculator.CalculateEncodedLeafBatch(start, end, references.AsSpan());
                            return;
                        }
                        if (Avx512F.IsSupported && valueLength is > MaxSingleBlockValueLength and <= MaxMultiBlockValueLength)
                        {
                            calculator.CalculateMultiBlockLeafBatch(start, end, references.AsSpan());
                            return;
                        }
                    }
                    for (int position = start; position < end; position++)
                    {
                        Key key = calculator.GetKey(position);
                        int depth = position == 0 ? 0 : CommonPrefix(key, calculator.GetKey(position - 1), 0);
                        if (position + 1 < inputs.Count)
                            depth = Math.Max(depth, CommonPrefix(key, calculator.GetKey(position + 1), 0));
                        references[position] = calculator.Leaf(key, depth + 1, inputs[calculator.GetIndex(position)]);
                    }
                });
            }
            catch (AggregateException exception)
            {
                ExceptionDispatchInfo.Throw(exception.InnerExceptions[0]);
            }
            // Avoid scanning tiny-value tries when the first full branch is already ineligible.
            if (Avx512F.IsSupported && _items.Length >= (BranchBatchSize + 1) * BranchChildCount
                && references[BranchChildCount - 1].Length == Keccak.Size)
                BatchTerminalBranches(references.AsSpan());
            return new Calculator<T, TEncoder>(_items, encoder, references.AsSpan()).CalculateSequential();
        }

        [SkipLocalsInit]
        private void CalculateMultiBlockLeafBatch(int start, int end, Span<NodeReference> references)
        {
            const int metadataLength = LeafBatchSize * sizeof(int);
            const int pathsLength = LeafBatchSize * 6;
            const int hashesOffset = 3 * metadataLength + pathsLength;
            const int positionsOffset = hashesOffset + 8 * Keccak.Size;
            const int indicesLength = 8 * sizeof(int);
            Debug.Assert(end - start <= LeafBatchSize);
            Unsafe.SkipInit(out MultiBlockLeafBuffer scratch);
            Span<byte> storage = MemoryMarshal.AsBytes((Span<Vector256<byte>>)scratch);
            Span<int> paddedLengths = MemoryMarshal.Cast<byte, int>(storage[..metadataLength]);
            Span<int> valueLengths = MemoryMarshal.Cast<byte, int>(storage.Slice(metadataLength, metadataLength));
            Span<int> pathLengths = MemoryMarshal.Cast<byte, int>(storage.Slice(2 * metadataLength, metadataLength));
            Span<byte> paths = storage.Slice(3 * metadataLength, pathsLength);
            int maxPaddedLength = 0;
            for (int position = start; position < end; position++)
            {
                int slot = position - start;
                Key key = GetKey(position);
                int depth = position == 0 ? 0 : CommonPrefix(key, GetKey(position - 1), 0);
                if (position + 1 < _items.Length)
                    depth = Math.Max(depth, CommonPrefix(key, GetKey(position + 1), 0));
                T item = _items[GetIndex(position)];
                ReadOnlySpan<byte> value = encoder.GetEncodedValue(item);
                int valueLength = value.IsEmpty ? encoder.GetLength(item) : value.Length;
                paddedLengths[slot] = 0;
                if (valueLength <= MaxSingleBlockValueLength || valueLength > MaxMultiBlockValueLength)
                {
                    references[position] = Leaf(key, depth + 1, item);
                    continue;
                }
                Span<byte> path = paths.Slice(slot * 6, 6);
                int pathLength = EncodePath(key, depth + 1, key.Length - depth - 1, isLeaf: true, path);
                int contentLength = Rlp.LengthOf(path[..pathLength]) + Rlp.LengthOfByteString(valueLength, 128);
                int paddedLength = (Rlp.LengthOfSequence(contentLength) / 136 + 1) * 136;
                paddedLengths[slot] = paddedLength;
                valueLengths[slot] = valueLength;
                pathLengths[slot] = pathLength;
                maxPaddedLength = Math.Max(maxPaddedLength, paddedLength);
            }
            if (maxPaddedLength == 0) return;
            using ArrayPoolDisposableReturn rental = ArrayPoolDisposableReturn.Rent(8 * maxPaddedLength, out byte[] buffer);
            Span<byte> hashes = storage.Slice(hashesOffset, 8 * Keccak.Size);
            Span<int> positions = MemoryMarshal.Cast<byte, int>(storage.Slice(positionsOffset, indicesLength));
            Span<int> lengths = MemoryMarshal.Cast<byte, int>(storage.Slice(positionsOffset + indicesLength, indicesLength));
            for (int first = 0; first < end - start; first++)
            {
                int paddedLength = paddedLengths[first];
                if (paddedLength == 0) continue;
                int pending = 0;
                for (int slot = first; slot < end - start; slot++)
                {
                    if (paddedLengths[slot] != paddedLength) continue;
                    paddedLengths[slot] = 0;
                    int position = start + slot;
                    T item = _items[GetIndex(position)];
                    ReadOnlySpan<byte> value = encoder.GetEncodedValue(item);
                    ReadOnlySpan<byte> path = paths.Slice(slot * 6, pathLengths[slot]);
                    int valueLength = valueLengths[slot];
                    int contentLength = Rlp.LengthOf(path) + Rlp.LengthOfByteString(valueLength, 128);
                    Span<byte> block = buffer.AsSpan(pending * paddedLength, paddedLength);
                    RlpWriter writer = new(block);
                    writer.StartSequence(contentLength);
                    writer.Encode(path);
                    if (value.IsEmpty)
                    {
                        writer.StartByteArray(valueLength, false);
                        encoder.Encode(ref writer, item);
                    }
                    else writer.Encode(value);
                    int totalLength = writer.Position;
                    block[totalLength..].Clear();
                    block[totalLength] = 0x01;
                    block[^1] |= 0x80;
                    positions[pending] = position;
                    lengths[pending++] = totalLength;
                    if (pending == 8)
                    {
                        HashMultiBlockLeaves(buffer, paddedLength, hashes, positions, lengths, references);
                        pending = 0;
                    }
                }
                if (pending != 0)
                    HashMultiBlockLeaves(buffer, paddedLength, hashes, positions[..pending], lengths, references);
            }
        }

        [SkipLocalsInit]
        private void CalculateEncodedLeafBatch(int start, int end, Span<NodeReference> references)
        {
            int batchSize = Avx512F.IsSupported ? 8 : 4;
            const int rate = 136;
            const int blocksLength = 8 * rate;
            const int hashesLength = 8 * Keccak.Size;
            const int indicesLength = 8 * sizeof(int);
            Unsafe.SkipInit(out LeafBatchBuffer buffer);
            Span<byte> storage = MemoryMarshal.AsBytes((Span<Vector256<byte>>)buffer);
            Span<byte> blocks = storage[..blocksLength];
            Span<byte> hashes = storage.Slice(blocksLength, hashesLength);
            Span<int> positions = MemoryMarshal.Cast<byte, int>(storage.Slice(blocksLength + hashesLength, indicesLength));
            Span<int> lengths = MemoryMarshal.Cast<byte, int>(storage.Slice(blocksLength + hashesLength + indicesLength, indicesLength));
            Span<byte> path = storage.Slice(blocksLength + hashesLength + 2 * indicesLength, 6);
            int pending = 0;
            for (int position = start; position < end; position++)
            {
                Key key = GetKey(position);
                int depth = position == 0 ? 0 : CommonPrefix(key, GetKey(position - 1), 0);
                if (position + 1 < _items.Length)
                    depth = Math.Max(depth, CommonPrefix(key, GetKey(position + 1), 0));
                T item = _items[GetIndex(position)];
                ReadOnlySpan<byte> value = encoder.GetEncodedValue(item);
                int valueLength = value.IsEmpty ? encoder.GetLength(item) : value.Length;
                if (valueLength < (typeof(T) == typeof(Withdrawal) ? 2 : Keccak.Size) || valueLength > MaxSingleBlockValueLength)
                {
                    references[position] = Leaf(key, depth + 1, item);
                    continue;
                }
                int pathLength = EncodePath(key, depth + 1, key.Length - depth - 1, isLeaf: true, path);
                Span<byte> block = blocks.Slice(pending * rate, rate);
                block.Clear();
                RlpWriter writer = new(block);
                writer.StartSequence(Rlp.LengthOf(path[..pathLength]) + Rlp.LengthOfByteString(valueLength, 128));
                writer.Encode(path[..pathLength]);
                if (value.IsEmpty)
                {
                    writer.StartByteArray(valueLength, false);
                    encoder.Encode(ref writer, item);
                }
                else writer.Encode(value);
                if (writer.Position < Keccak.Size)
                {
                    references[position] = NodeReference.FromRlp(block[..writer.Position]);
                    continue;
                }
                block[writer.Position] = 0x01;
                block[rate - 1] |= 0x80;
                lengths[pending] = writer.Position;
                positions[pending++] = position;
                if (pending == batchSize)
                {
                    if (Avx512F.IsSupported)
                        HashLeafBatchAvx512(ref buffer, references, batchSize);
                    else
                    {
                        KeccakHash.ComputePaddedBlocks4Avx2(ref MemoryMarshal.GetReference(blocks), ref MemoryMarshal.GetReference(hashes));
                        for (int i = 0; i < batchSize; i++)
                        {
                            ValueHash256 hash = default;
                            hashes.Slice(i * Keccak.Size, Keccak.Size).CopyTo(hash.BytesAsSpan);
                            references[positions[i]] = new NodeReference(hash, Keccak.Size);
                        }
                    }
                    pending = 0;
                }
            }
            if (Avx512F.IsSupported && pending == 0) return;
            if (Avx512F.IsSupported && pending >= 2)
            {
                blocks[(pending * rate)..].Clear();
                HashLeafBatchAvx512(ref buffer, references, pending);
                return;
            }
            for (int i = 0; i < pending; i++)
            {
                references[positions[i]] = NodeReference.FromRlp(blocks.Slice(i * rate, lengths[i]));
            }
        }

        private Hash256 CalculateSequential()
        {
            if (_leaves.IsEmpty && Avx2.IsSupported && CanBatchLeavesSequentially())
                return CalculateEncodedSequential();
            NodeReference root = _leaves.IsEmpty ? Build<OffFlag>(0, _items.Length, 0) : Build<OnFlag>(0, _items.Length, 0);
            return root.Length == 32 ? new Hash256(root.Value) : Keccak.Compute(root.Value.Bytes[..root.Length]);
        }

        [SkipLocalsInit]
        private NodeReference Build<TPrecomputed>(int start, int end, int depth) where TPrecomputed : struct, IFlag
        {
            Key first = GetKey(start);
            if (end - start == 1)
                return TPrecomputed.IsActive ? _leaves[start] : Leaf(first, depth, _items[GetIndex(start)]);

            Key last = GetKey(end - 1);
            int commonDepth = CommonPrefix(first, last, depth);

            Span<byte> encoded = stackalloc byte[BranchPrefixLength + MaxBranchContentLength];
            if (commonDepth != depth)
            {
                Span<byte> path = stackalloc byte[6];
                int pathLength = EncodePath(first, depth, commonDepth - depth, isLeaf: false, path);
                NodeReference child = Build<TPrecomputed>(start, end, commonDepth);
                int contentLength = Rlp.LengthOf(path[..pathLength]) + child.EncodedLength;
                int position = Rlp.StartSequence(encoded, 0, contentLength);
                position = Rlp.Encode(encoded, position, path[..pathLength]);
                position += child.WriteTo(encoded[position..]);
                return NodeReference.FromRlp(encoded[..position]);
            }

            // A completed terminal branch replaces its first leaf; extension paths above it still apply.
            if (Avx512F.IsSupported && TPrecomputed.IsActive && end - start == BranchChildCount && _leaves[start].Length == PrecomputedBranchLength)
                return new NodeReference(_leaves[start].Value, Keccak.Size);
            int offset = BranchPrefixLength;
            if (TPrecomputed.IsActive && first.Length == depth + 1 && last.Length == first.Length)
            {
                // Within a fixed-length RLP prefix, indexed keys have consecutive final nibbles.
                int firstNibble = first.Nibble(depth);
                Debug.Assert(end - start == last.Nibble(depth) - firstNibble + 1);
                encoded.Slice(offset, firstNibble).Fill(Rlp.EmptyByteArrayByte);
                offset += firstNibble;
                foreach (NodeReference child in _leaves.Slice(start, end - start))
                    offset += child.WriteTo(encoded[offset..]);
                int emptyChildren = 16 - firstNibble - (end - start);
                encoded.Slice(offset, emptyChildren).Fill(Rlp.EmptyByteArrayByte);
                offset += emptyChildren;
            }
            else
            {
                int cursor = start;
                for (int nibble = 0; nibble < 16; nibble++)
                {
                    if (cursor == end || GetKey(cursor).Nibble(depth) != nibble)
                    {
                        encoded[offset++] = Rlp.EmptyByteArrayByte;
                        continue;
                    }

                    int next = cursor + 1;
                    if (end - cursor >= 32)
                    {
                        int upper = end;
                        while (next < upper)
                        {
                            int middle = next + ((upper - next) >> 1);
                            if (GetKey(middle).Nibble(depth) == nibble) next = middle + 1;
                            else upper = middle;
                        }
                    }
                    else
                    {
                        while (next < end && GetKey(next).Nibble(depth) == nibble) next++;
                    }
                    NodeReference child = Build<TPrecomputed>(cursor, next, depth + 1);
                    offset += child.WriteTo(encoded[offset..]);
                    cursor = next;
                }
            }
            encoded[offset++] = Rlp.EmptyByteArrayByte;
            int branchLength = offset - BranchPrefixLength;
            int prefixLength = Rlp.StartSequence(encoded, 0, branchLength);
            Debug.Assert(branchLength <= MaxBranchContentLength);
            Debug.Assert(prefixLength <= BranchPrefixLength);
            encoded.Slice(BranchPrefixLength, branchLength).CopyTo(encoded[prefixLength..]);
            return NodeReference.FromRlp(encoded[..(prefixLength + branchLength)]);
        }

        [SkipLocalsInit]
        private NodeReference Leaf(Key key, int depth, T item)
        {
            Span<byte> path = stackalloc byte[6];
            int pathLength = EncodePath(key, depth, key.Length - depth, isLeaf: true, path);
            ReadOnlySpan<byte> encodedValue = encoder.GetEncodedValue(item);
            int valueLength = encodedValue.IsEmpty ? encoder.GetLength(item) : encodedValue.Length;
            Debug.Assert(valueLength > 0, "Empty encodings require trie deletion semantics.");
            Span<byte> shortValue = stackalloc byte[1];
            if (valueLength == 1)
            {
                RlpWriter shortWriter = new(shortValue);
                if (encodedValue.IsEmpty) encoder.Encode(ref shortWriter, item);
                else shortValue[0] = encodedValue[0];
            }

            bool unprefixedByte = valueLength == 1 && shortValue[0] < 128;
            int encodedValueLength = Rlp.LengthOfByteString(valueLength, unprefixedByte ? (byte)0 : (byte)128);
            int contentLength = Rlp.LengthOf(path[..pathLength]) + encodedValueLength;
            int totalLength = Rlp.LengthOfSequence(contentLength);
            using ArrayPoolDisposableReturn rental = ArrayPoolDisposableReturn.Rent(totalLength, out byte[] buffer);
            RlpWriter writer = new(buffer.AsSpan(0, totalLength));
            writer.StartSequence(contentLength);
            writer.Encode(path[..pathLength]);
            if (valueLength <= 1)
            {
                writer.Encode(shortValue[..valueLength]);
            }
            else
            {
                if (encodedValue.IsEmpty)
                {
                    writer.StartByteArray(valueLength, false);
                    encoder.Encode(ref writer, item);
                }
                else writer.Encode(encodedValue);
            }
            Debug.Assert(writer.Position == totalLength);
            return NodeReference.FromRlp(buffer.AsSpan(0, writer.Position));
        }

        private int GetIndex(int position)
        {
            int zeroPosition = Math.Min(_items.Length - 1, 127);
            return position < zeroPosition ? position + 1 : position == zeroPosition ? 0 : position;
        }

        private Key GetKey(int position)
        {
            uint index = (uint)GetIndex(position);
            if (index < 128) return new(index == 0 ? 128 : index, 2);
            int byteCount = (32 - BitOperations.LeadingZeroCount(index) + 7) / 8;
            return new(((ulong)(128 + byteCount) << (byteCount * 8)) | index, (byteCount + 1) * 2);
        }
    }

    private static void HashMultiBlockLeaves(byte[] buffer, int paddedLength, Span<byte> hashes,
        ReadOnlySpan<int> positions, ReadOnlySpan<int> lengths, Span<NodeReference> references)
    {
        if (positions.Length < 2)
        {
            for (int i = 0; i < positions.Length; i++)
                references[positions[i]] = NodeReference.FromRlp(buffer.AsSpan(i * paddedLength, lengths[i]));
            return;
        }
        buffer.AsSpan(positions.Length * paddedLength, (8 - positions.Length) * paddedLength).Clear();
        KeccakHash.ComputePaddedMultiBlocks8Avx512(ref buffer[0], paddedLength, ref hashes[0]);
        ReadOnlySpan<ValueHash256> values = MemoryMarshal.Cast<byte, ValueHash256>(hashes);
        for (int i = 0; i < positions.Length; i++)
        {
            ValueHash256 hash = values[i];
            references[positions[i]] = new NodeReference(hash, Keccak.Size);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashLeafBatchAvx512(ref LeafBatchBuffer buffer, Span<NodeReference> references, int count)
    {
        const int blocksLength = 8 * 136;
        const int hashesLength = 8 * Keccak.Size;
        Span<byte> storage = MemoryMarshal.AsBytes((Span<Vector256<byte>>)buffer);
        ReadOnlySpan<ValueHash256> hashes = MemoryMarshal.Cast<byte, ValueHash256>(storage.Slice(blocksLength, hashesLength))[..count];
        ReadOnlySpan<int> positions = MemoryMarshal.Cast<byte, int>(storage.Slice(blocksLength + hashesLength, 8 * sizeof(int)))[..count];
        KeccakHash.ComputePaddedBlocks8Avx512(ref storage[0], ref storage[blocksLength]);
        for (int i = 0; i < hashes.Length; i++)
            references[positions[i]] = new NodeReference(hashes[i], Keccak.Size);
    }

    private static int CommonPrefix(Key first, Key last, int depth)
    {
        while (depth < Math.Min(first.Length, last.Length) && first.Nibble(depth) == last.Nibble(depth)) depth++;
        return depth;
    }

    private static int EncodePath(Key key, int depth, int length, bool isLeaf, Span<byte> output)
    {
        int end = depth + length;
        byte flags = isLeaf ? (byte)32 : (byte)0;
        output[0] = (length & 1) != 0 ? (byte)(flags | 16 | key.Nibble(depth++)) : flags;
        int position = 1;
        while (depth < end)
        {
            output[position++] = (byte)((key.Nibble(depth) << 4) | key.Nibble(depth + 1));
            depth += 2;
        }
        return position;
    }

    private readonly record struct Key(ulong Value, int Length)
    {
        public int Nibble(int depth) => (int)(Value >> ((Length - depth - 1) * 4)) & 15;
    }

    internal readonly record struct NodeReference(ValueHash256 Value, int Length)
    {
        public int EncodedLength => Length == 32 ? 33 : Length;

        public int WriteTo(Span<byte> output)
        {
            if (Length == 32)
            {
                output[0] = 160;
                Value.Bytes.CopyTo(output[1..]);
                return 33;
            }
            Value.Bytes[..Length].CopyTo(output);
            return Length;
        }

        public static NodeReference FromRlp(ReadOnlySpan<byte> encoded)
        {
            ValueHash256 value = default;
            if (encoded.Length < 32)
            {
                encoded.CopyTo(value.BytesAsSpan);
                return new(value, encoded.Length);
            }
            KeccakHash.ComputeHash(encoded, value.BytesAsSpan);
            return new(value, 32);
        }
    }
}
