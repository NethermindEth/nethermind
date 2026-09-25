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

        /// <summary>The hash of the node <paramref name="frame"/> stores or leaves implicit at <paramref name="position"/>, computed once.</summary>
        internal ValueHash256 GetHash<TFrame>(ref TFrame frame, int position, TrieUpdaterMetrics? metrics)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            uint bit = 1u << position;
            if ((_known & bit) != 0) return _hashes[position];
            ReadOnlyMemory<byte> encoding = frame.GetEncoding(position);
            ValueHash256 hash = default;
            if (!encoding.IsEmpty)
            {
                metrics?.IncrementNodeHashes();
                hash = PbtNodeCodec.Hash(PbtNodeReader.FromValidated(encoding.Span));
            }
            else if (PbtFourLevelGroupGeometry.WidthOf(position) is int width and > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
            {
                GetChildHashes(ref frame, position - width, position - 1, metrics, out ValueHash256 left, out ValueHash256 right);
                if (left != default && right != default)
                {
                    Span<byte> branch = stackalloc byte[67];
                    PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
                    metrics?.IncrementNodeHashes();
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
        internal void GetChildHashes<TFrame>(ref TFrame frame, int leftPosition, int rightPosition, TrieUpdaterMetrics? metrics,
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
                    metrics?.AddNodeHashes(2);
                    Blake3Hash.HashTwo(PbtNodeReader.FromValidated(leftEncoding.Span).Preimage, PbtNodeReader.FromValidated(rightEncoding.Span).Preimage, out left, out right);
                    _hashes[leftPosition] = left;
                    _hashes[rightPosition] = right;
                    _known |= bits;
                    return;
                }
            }
            left = GetHash(ref frame, leftPosition, metrics);
            right = GetHash(ref frame, rightPosition, metrics);
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct HashBuffer
        {
            private ValueHash256 _element;
        }
    }
}
