// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Merkleization;

public readonly struct MerkleTreeNode
{
    public MerkleTreeNode(Bytes32 hash, ulong index)
    {
        Hash = hash;
        Index = index;
    }

    public Bytes32 Hash { get; }
    public ulong Index { get; } // 32bit index for 32 depth of a tree

    public override string ToString()
    {
        return $"{Hash.Unwrap().ToHexString(true)}, {Index}";
    }
}
