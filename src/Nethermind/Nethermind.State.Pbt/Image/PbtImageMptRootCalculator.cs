// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.Image;

internal static class PbtImageMptRootCalculator
{
    internal static ValueHash256 Calculate(IEnumerable<KeyValuePair<ValueHash256, byte[]>> entries, CancellationToken cancellationToken)
    {
        using IEnumerator<KeyValuePair<ValueHash256, byte[]>> enumerator = entries.GetEnumerator();
        Builder builder = new(enumerator, cancellationToken);
        return builder.HasCurrent ? ValueKeccak.Compute(builder.ReadNode(0).Encode(0)) : Keccak.EmptyTreeHash.ValueHash256;
    }

    // Yellow Paper Appendix D: defer unary paths until a branch or leaf is known, then
    // hex-prefix encode them. Only the active 64-nibble frontier and one input remain live.
    private sealed class Builder(IEnumerator<KeyValuePair<ValueHash256, byte[]>> enumerator, CancellationToken cancellationToken)
    {
        public bool HasCurrent { get; private set; } = MoveFirst(enumerator, cancellationToken);
        private int _commonPrefix;

        public Node ReadNode(int depth)
        {
            ValueHash256 firstKey = enumerator.Current.Key;
            if (depth == 64)
            {
                Node leaf = new(firstKey, enumerator.Current.Value, depth);
                cancellationToken.ThrowIfCancellationRequested();
                HasCurrent = enumerator.MoveNext();
                if (HasCurrent)
                {
                    ValueHash256 nextKey = enumerator.Current.Key;
                    if (firstKey.Bytes.SequenceCompareTo(nextKey.Bytes) >= 0)
                        throw new InvalidDataException("MPT keys must be strictly increasing.");

                    _commonPrefix = 0;
                    while (Nibble(firstKey, _commonPrefix) == Nibble(nextKey, _commonPrefix))
                        _commonPrefix++;
                }
                return leaf;
            }

            Node firstChild = ReadNode(depth + 1);
            if (!HasCurrent || _commonPrefix < depth)
                return firstChild;

            Span<byte> children = stackalloc byte[16 * Rlp.LengthOfKeccakRlp + 1];
            int position = 0;
            int childIndex = Nibble(firstKey, depth);
            children[..childIndex].Fill(0x80);
            position += childIndex;
            position += WriteReference(children[position..], firstChild.Encode(depth + 1));
            childIndex++;

            while (HasCurrent && _commonPrefix == depth)
            {
                int nextIndex = Nibble(enumerator.Current.Key, depth);
                children.Slice(position, nextIndex - childIndex).Fill(0x80);
                position += nextIndex - childIndex;
                Node child = ReadNode(depth + 1);
                position += WriteReference(children[position..], child.Encode(depth + 1));
                childIndex = nextIndex + 1;
            }

            children.Slice(position, 17 - childIndex).Fill(0x80);
            position += 17 - childIndex;
            byte[] encoded = new byte[Rlp.LengthOfSequence(position)];
            int contentStart = Rlp.StartSequence(encoded, 0, position);
            children[..position].CopyTo(encoded.AsSpan(contentStart));
            return new Node(firstKey, encoded, depth);
        }

        private static bool MoveFirst(IEnumerator<KeyValuePair<ValueHash256, byte[]>> enumerator, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return enumerator.MoveNext();
        }
    }

    private readonly record struct Node(ValueHash256 Key, byte[] Data, int Depth)
    {
        public byte[] Encode(int depth)
        {
            bool isLeaf = Depth == 64;
            if (!isLeaf && depth == Depth)
                return Data;

            byte[] path = new byte[Depth - depth];
            for (int index = 0; index < path.Length; index++)
                path[index] = Nibble(Key, depth + index);
            byte[] compactPath = HexPrefix.ToBytes(path, isLeaf);
            int valueLength = isLeaf ? Rlp.LengthOf(Data.AsSpan()) : ReferenceLength(Data);
            int contentLength = Rlp.LengthOf(compactPath.AsSpan()) + valueLength;
            byte[] encoded = new byte[Rlp.LengthOfSequence(contentLength)];
            int position = Rlp.StartSequence(encoded, 0, contentLength);
            position = Rlp.Encode(encoded, position, compactPath);
            if (isLeaf)
                Rlp.Encode(encoded, position, Data);
            else
                WriteReference(encoded.AsSpan(position), Data);
            return encoded;
        }
    }

    private static byte Nibble(ValueHash256 key, int depth) => (byte)((key.Bytes[depth / 2] >> ((depth & 1) == 0 ? 4 : 0)) & 15);

    private static int ReferenceLength(byte[] encoded) => encoded.Length < 32 ? encoded.Length : Rlp.LengthOfKeccakRlp;

    private static int WriteReference(Span<byte> destination, byte[] encoded)
    {
        if (encoded.Length < 32)
        {
            encoded.CopyTo(destination);
            return encoded.Length;
        }

        ValueHash256 hash = ValueKeccak.Compute(encoded);
        return Rlp.Encode(destination, 0, hash.Bytes);
    }
}
