// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    // The only stack buffer, the branch encoding in GetHash, is fully written before it is read.
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
            ReadOnlyMemory<byte> encoding = frame.GetEncoding(position);
            ValueHash256 hash = default;
            if (!encoding.IsEmpty)
            {
                hash = PbtNodeCodec.Hash(PbtNodeReader.FromValidated(encoding.Span));
            }
            else if (PbtFourLevelGroupGeometry.WidthOf(position) is int width and > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
            {
                GetChildHashes(ref frame, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
                if (left != default && right != default)
                {
                    Span<byte> branch = stackalloc byte[67];
                    PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
                    hash = Blake3Hash.Hash(branch);
                }
            }
            _hashes[position] = hash;
            _known |= bit;
            return hash;
        }

        /// <summary>
        /// <see cref="GetHash"/> for both children of an omitted branch; two stored encodings that still need
        /// hashing are hashed together.
        /// </summary>
        internal void GetChildHashes<TFrame>(ref TFrame frame, int leftPosition, int rightPosition,
            out ValueHash256 left, out ValueHash256 right)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            uint bits = (1u << leftPosition) | (1u << rightPosition);
            if ((_known & bits) == 0)
            {
                ReadOnlyMemory<byte> leftEncoding = frame.GetEncoding(leftPosition);
                ReadOnlyMemory<byte> rightEncoding = frame.GetEncoding(rightPosition);
                if (!leftEncoding.IsEmpty && !rightEncoding.IsEmpty)
                {
                    Blake3Hash.HashTwo(PbtNodeReader.FromValidated(leftEncoding.Span).Preimage, PbtNodeReader.FromValidated(rightEncoding.Span).Preimage, out left, out right);
                    _hashes[leftPosition] = left;
                    _hashes[rightPosition] = right;
                    _known |= bits;
                    return;
                }
            }
            left = GetHash(ref frame, leftPosition);
            right = GetHash(ref frame, rightPosition);
        }

        /// <summary>The hash already known for <paramref name="position"/>, or default when it has not been computed or seeded.</summary>
        internal readonly ValueHash256 KnownHash(int position) => (_known & (1u << position)) != 0 ? _hashes[position] : default;

        /// <summary>Records the hash a parent's link holds for the node at <paramref name="position"/>, so it is never computed.</summary>
        internal void Seed(int position, in ValueHash256 hash)
        {
            _hashes[position] = hash;
            _known |= 1u << position;
        }

        /// <summary><see cref="GetChildHashes"/> that also pairs the hashing of every omitted level below the two children.</summary>
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
            if (!leftPreimage.IsEmpty && !rightPreimage.IsEmpty)
            {
                Blake3Hash.HashTwo(leftPreimage, rightPreimage, out _hashes[leftPosition], out _hashes[rightPosition]);
            }
            else if (!leftPreimage.IsEmpty)
            {
                _hashes[leftPosition] = Blake3Hash.Hash(leftPreimage);
            }
            else if (!rightPreimage.IsEmpty)
            {
                _hashes[rightPosition] = Blake3Hash.Hash(rightPreimage);
            }
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
            if (!encoding.IsEmpty) return PbtNodeReader.FromValidated(encoding.Span).Preimage;
            _hashes[position] = default;
            if (PbtFourLevelGroupGeometry.WidthOf(position) is not (int width and > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)) return default;
            GetChildHashesPaired(ref frame, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
            if (left == default || right == default) return default;
            PbtNodeCodec.CreateBranchEncoding(buffer, 0, left, right);
            return buffer;
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct HashBuffer
        {
            private ValueHash256 _element;
        }
    }
}
