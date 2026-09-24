// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>A stored branch borrowed from an open frame, to be written out again byte for byte.</summary>
    /// <remarks>
    /// Composition never touched this node, so the group it is written into keeps the bytes the group it was read from
    /// holds, and its hash is the one decomposition seeded from the link that named it. The copy only holds while the
    /// position addressing it does not move: a node promoted by an updated sibling's deletion, or a group root placed
    /// against a shallower cursor, branches at the same depth over a longer compressed prefix and is rebuilt from its
    /// stored children. The encoding is borrowed, so it is only valid while that frame is open.
    /// </remarks>
    internal readonly struct DirectCopySubtree
    {
        private readonly ReadOnlyMemory<byte> _encoding;
        private readonly ValueHash256 _knownHash;

        /// <param name="path">The group-local path of the position the node is stored at.</param>
        /// <param name="knownHash">The hash the node's link held, or default when no link addressed it.</param>
        internal DirectCopySubtree(ReadOnlyMemory<byte> encoding, NodeGroupPath path, in ValueHash256 knownHash)
        {
            Debug.Assert(!PbtNodeReader.FromValidated(encoding.Span).IsLeaf, "A direct copy carries a stored branch encoding.");
            _encoding = encoding;
            _knownHash = knownHash;
            Path = path;
        }

        /// <summary>The group-local path of the position the node is stored at.</summary>
        internal NodeGroupPath Path { get; }
        internal bool IsEmpty => _encoding.IsEmpty;
        /// <summary>The stored length, which is what writing the node at its own anchor emits.</summary>
        internal int Length => _encoding.Length;
        internal PbtNodeReader Reader => PbtNodeReader.FromValidated(_encoding.Span);
        internal CompressedPrefix Prefix => Reader.Prefix;

        internal void CopyTo(Span<byte> destination) => _encoding.Span.CopyTo(destination);

        /// <summary>The hash of the stored encoding.</summary>
        /// <remarks>A node below a level left implicit carries no link, so it is hashed here the once instead.</remarks>
        internal ValueHash256 Hash(TrieUpdaterMetrics? metrics)
        {
            if (_knownHash != default) return _knownHash;
            metrics?.IncrementNodeHashes();
            return PbtNodeCodec.Hash(Reader);
        }
    }
}
