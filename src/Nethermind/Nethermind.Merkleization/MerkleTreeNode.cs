// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Merkleization;

public readonly struct MerkleTreeNode(Bytes32 hash, ulong index)
{
    public Bytes32 Hash { get; } = hash;
    public ulong Index { get; } = index;

    public override string ToString()
    {
        return $"{Hash.Unwrap().ToHexString(true)}, {Index}";
    }
}
