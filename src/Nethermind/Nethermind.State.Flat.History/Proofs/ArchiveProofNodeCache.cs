// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class ArchiveProofNodeCache(int capacity)
{
    public const int PathCached = int.MaxValue;

    private const byte LeafFlag = 0x20;

    private readonly ClockCache<ValueHash256, byte[]> _nodes = new(capacity);

    public bool TryGet(in ValueHash256 hash, out byte[]? rlp) => _nodes.TryGet(hash, out rlp);

    public void Set(in ValueHash256 hash, byte[] rlp) => _nodes.Set(hash, rlp);

    /// <summary>
    /// Follows <paramref name="fullPath"/> down from <paramref name="root"/> through cached nodes only and returns the depth
    /// of the first node that is not cached, or <see cref="PathCached"/> when the path ends in a cached leaf or an empty
    /// branch slot. An extension or an inline child stops the walk at the depth below it.
    /// </summary>
    public int FirstUncachedDepth(in ValueHash256 root, in ValueHash256 fullPath, out byte[]? leaf)
    {
        leaf = null;
        TreePath path = new(fullPath, CommitmentDepthPolicy.MaxTrieDepth);
        ValueHash256 hash = root;
        for (int depth = 0; depth < CommitmentDepthPolicy.MaxTrieDepth; depth++)
        {
            if (!_nodes.TryGet(hash, out byte[]? rlp) || rlp is null) return depth;

            if (!BranchRlp.TryReadChild(rlp, path[depth], out int referenceLength, out hash))
            {
                if (!IsLeaf(rlp)) return depth + 1;

                leaf = rlp;
                return PathCached;
            }

            if (referenceLength == 0) return PathCached;
            if (referenceLength != Hash256.Size) return depth + 1;
        }

        return PathCached;
    }

    private static bool IsLeaf(ReadOnlySpan<byte> nodeRlp)
    {
        RlpReader reader = new(nodeRlp);
        reader.ReadSequenceLength();
        ReadOnlySpan<byte> key = reader.DecodeByteArraySpan();
        return !key.IsEmpty && (key[0] & LeafFlag) != 0;
    }
}
