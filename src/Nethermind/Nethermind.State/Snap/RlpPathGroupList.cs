// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap;

public sealed class RlpPathGroupList(IRlpItemList inner) : DecodeOnDemandRlpItemList<PathGroup>(inner)
{
    protected override PathGroup DecodeItem(int index)
    {
        using IRlpItemList group = Inner.GetNestedItemList(index);
        byte[][] paths = new byte[group.Count][];
        for (int j = 0; j < group.Count; j++)
        {
            // A path no trie node can sit at is served as a missing node, so it is not copied: a peer can send it
            // as large as the message allows.
            ReadOnlySpan<byte> path = group.ReadContent(j);
            paths[j] = path.Length > PathGroup.MaxPathLength ? PathGroup.TooLongPath : path.ToArray();
        }
        return new PathGroup { Group = paths };
    }
}
