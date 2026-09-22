// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public sealed partial class TrieNode
{
    private const byte RetainedMask = 0b0100_0000;

    internal bool IsRetained => (ReadBlockAndFlags() & RetainedMask) != 0;

    /// <summary>Copies materialized descendants without resolving additional nodes.</summary>
    /// <remarks>The detached graph cannot acquire or lose child references after publication.
    /// Its recursive memory estimate remains stable, and ordinary copy-on-write updates produce mutable nodes.</remarks>
    internal TrieNode? CreateRetainedSubtree(int depth, ref int remainingNodes)
    {
        if (remainingNodes == 0 || IsDirty || _nodeData is null || (IsWarmerOwned && !IsWarmerResolved)) return null;

        CappedArray<byte> rlp = ReadRlp();
        if (rlp.IsNull || (IsWarmerOwned && Keccak is { } hash && ValueKeccak.Compute(rlp.AsSpan()) != hash)) return null;

        remainingNodes--;
        TrieNode copy = new(this)
        {
            Keccak = Keccak,
            _blockAndFlags = (byte)((ReadBlockAndFlags() & _persistedMask) | RetainedMask)
        };
        copy.InitRlp(rlp);

        INodeData data = copy._nodeData!;
        int childCount = copy.IsBranch ? BranchesCount : copy.IsExtension ? 1 : 0;
        for (int i = 0; i < childCount; i++)
        {
            if (data[i] is TrieNode child)
            {
                // A missing reference is reconstructed from this node's RLP when traversed.
                data[i] = depth > 0 ? child.CreateRetainedSubtree(depth - 1, ref remainingNodes) : null;
            }
        }

        return copy;
    }
}
