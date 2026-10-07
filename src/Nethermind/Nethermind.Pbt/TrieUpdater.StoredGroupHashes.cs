// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The hashes of the nodes one stored group holds that the fold computes from their encodings, each computed once.</summary>
    /// <remarks>
    /// Kept beside a frame from its decomposition to its composition. A group that is not stored holds no node, so its
    /// hashes are never read.
    /// </remarks>
    // The only stack buffers, the branch encodings PendingPreimage rebuilds, are fully written before they are read.
    [SkipLocalsInit]
    internal struct StoredGroupHashes
    {
        private HashBuffer _hashes;
        private uint _known;

        /// <summary>Opens <paramref name="hashes"/> with no hash known, leaving the hash buffer unzeroed, since only known positions are read from it.</summary>
        /// <remarks>A frame opens one per group it folds, and the buffer is a kilobyte.</remarks>
        internal static void Open(out StoredGroupHashes hashes)
        {
            Unsafe.SkipInit(out hashes);
            hashes._known = 0;
        }

        /// <summary>The hash of the node <paramref name="frame"/> stores or leaves implicit at <paramref name="position"/>, computed once.</summary>
        internal ValueHash256 GetHash<TFrame>(ref TFrame frame, int position)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            uint bit = 1u << position;
            if ((_known & bit) != 0) return _hashes[position];
            Span<byte> buffer = stackalloc byte[PbtNodeCodec.BranchPreimageLength(0)];
            ReadOnlySpan<byte> preimage = PendingPreimage(ref frame, position, buffer);
            ValueHash256 hash = preimage.IsEmpty ? default : Blake3Hash.Hash(preimage);
            _hashes[position] = hash;
            _known |= bit;
            return hash;
        }

        /// <summary>The hash already known for <paramref name="position"/>, or default when it has not been computed or seeded.</summary>
        internal readonly ValueHash256 KnownHash(int position) => (_known & (1u << position)) != 0 ? _hashes[position] : default;

        /// <summary>Records the hash a parent's link holds for the node at <paramref name="position"/>, so it is never computed.</summary>
        internal void Seed(int position, in ValueHash256 hash)
        {
            _hashes[position] = hash;
            _known |= 1u << position;
        }

        /// <summary><see cref="GetHash"/> for both children of an omitted branch, hashing the two together and pairing the hashing of every omitted level below them.</summary>
        /// <remarks>
        /// An omitted child is rebuilt from its own children first, so siblings at every level are hashed together
        /// rather than only the stored ones at the bottom.
        /// </remarks>
        internal void GetChildHashesPaired<TFrame>(ref TFrame frame, int leftPosition, int rightPosition,
            out ValueHash256 left, out ValueHash256 right)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            Span<byte> leftBuffer = stackalloc byte[PbtNodeCodec.BranchPreimageLength(0)];
            Span<byte> rightBuffer = stackalloc byte[PbtNodeCodec.BranchPreimageLength(0)];
            ReadOnlySpan<byte> leftPreimage = PendingPreimage(ref frame, leftPosition, leftBuffer);
            ReadOnlySpan<byte> rightPreimage = PendingPreimage(ref frame, rightPosition, rightBuffer);
            HashPendingPair(leftPreimage, ref _hashes[leftPosition], rightPreimage, ref _hashes[rightPosition]);
            _known |= (1u << leftPosition) | (1u << rightPosition);
            left = _hashes[leftPosition];
            right = _hashes[rightPosition];
        }

        /// <summary>The preimage <paramref name="position"/> still needs hashing, or empty when its hash is known or it holds no node.</summary>
        private ReadOnlySpan<byte> PendingPreimage<TFrame>(ref TFrame frame, int position, Span<byte> buffer)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            if ((_known & (1u << position)) != 0) return default;
            ReadOnlyMemory<byte> encoding = frame.GetEncoding(position);
            if (!encoding.IsEmpty)
            {
                PbtNodeReader node = PbtNodeReader.FromValidated(encoding.Span);
                Debug.Assert(!node.IsLeaf, "A root leaf's hash is the tree root and cannot be derived from its encoding.");
                return node.Preimage;
            }
            _hashes[position] = default;
            if (PbtFourLevelGroupGeometry.WidthOf(position) is not (int width and > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)) return default;
            GetChildHashesPaired(ref frame, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
            if (left == default || right == default) return default;
            PbtNodeCodec.CreateBranchEncoding(buffer, 0, left, right);
            return buffer;
        }

        [InlineArray(PbtFourLevelGroupGeometry.PositionCount)]
        private struct HashBuffer
        {
            private ValueHash256 _element;
        }
    }
}
