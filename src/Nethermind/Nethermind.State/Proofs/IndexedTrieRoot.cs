// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Proofs;

// The indexed tries from Yellow Paper section 4.4 have prefix-free RLP integer keys,
// ordered 1..127, 0, 128..N, so each subtree can be hashed before encoding its sibling.
internal static class IndexedTrieRoot
{
    internal const int MinItemsForParallelRootHash = 64;

    internal interface IValueEncoder<T>
    {
        ReadOnlySpan<byte> GetEncodedValue(T item);
        int GetLength(T item);
        void Encode<TWriter>(ref TWriter writer, T item) where TWriter : struct, IRlpWriteBackend, allows ref struct;
    }

    internal readonly ref struct Calculator<T, TEncoder>(ReadOnlySpan<T> items, TEncoder encoder,
        ReadOnlySpan<NodeReference> leaves = default) where TEncoder : struct, IValueEncoder<T>
    {
        private const int LeafBatchSize = 16;
        private const int BranchPrefixLength = 3;
        private const int MaxBranchContentLength = 16 * Rlp.LengthOfKeccakRlp + 1;
        private readonly ReadOnlySpan<T> _items = items;
        private readonly ReadOnlySpan<NodeReference> _leaves = leaves;

        public Hash256 Calculate(bool canBeParallel = true)
            => _items.IsEmpty ? Keccak.EmptyTreeHash
                : !canBeParallel || RuntimeInformation.IsSingleProcessor || _items.Length <= MinItemsForParallelRootHash
                ? CalculateSequential()
                : CalculateParallel();

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
            return new Calculator<T, TEncoder>(_items, encoder, references.AsSpan()).CalculateSequential();
        }

        private Hash256 CalculateSequential()
        {
            NodeReference root = Build(0, _items.Length, 0);
            return root.Length == 32 ? new Hash256(root.Value) : Keccak.Compute(root.Value.Bytes[..root.Length]);
        }

        [SkipLocalsInit]
        private NodeReference Build(int start, int end, int depth)
        {
            Key first = GetKey(start);
            if (end - start == 1)
                return _leaves.IsEmpty ? Leaf(first, depth, _items[GetIndex(start)]) : _leaves[start];

            Key last = GetKey(end - 1);
            int commonDepth = CommonPrefix(first, last, depth);

            Span<byte> encoded = stackalloc byte[BranchPrefixLength + MaxBranchContentLength];
            if (commonDepth != depth)
            {
                Span<byte> path = stackalloc byte[6];
                int pathLength = EncodePath(first, depth, commonDepth - depth, isLeaf: false, path);
                NodeReference child = Build(start, end, commonDepth);
                int contentLength = Rlp.LengthOf(path[..pathLength]) + child.EncodedLength;
                int position = Rlp.StartSequence(encoded, 0, contentLength);
                position = Rlp.Encode(encoded, position, path[..pathLength]);
                position += child.WriteTo(encoded[position..]);
                return NodeReference.FromRlp(encoded[..position]);
            }

            int cursor = start;
            int offset = BranchPrefixLength;
            for (int nibble = 0; nibble < 16; nibble++)
            {
                if (cursor == end || GetKey(cursor).Nibble(depth) != nibble)
                {
                    encoded[offset++] = Rlp.EmptyByteArrayByte;
                    continue;
                }

                int next = cursor + 1;
                while (next < end && GetKey(next).Nibble(depth) == nibble) next++;
                NodeReference child = Build(cursor, next, depth + 1);
                offset += child.WriteTo(encoded[offset..]);
                cursor = next;
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
