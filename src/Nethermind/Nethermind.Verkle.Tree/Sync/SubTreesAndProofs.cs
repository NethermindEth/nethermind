// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Verkle.Tree.Sync;

public class SubTreesAndProofs
{
    public SubTreesAndProofs(PathWithSubTree[] data, byte[] proofs)
    {
        SubTrees = data;
        Proofs = proofs;
    }

    public PathWithSubTree[] SubTrees { get; set; }
    public byte[] Proofs { get; set; }
}
