// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap;

public sealed class RlpPathGroupList(RlpItemList inner) : DecodeOnDemandRlpItemList<PathGroup>(inner)
{
    protected override PathGroup DecodeItem(int index)
    {
        using RlpItemList group = Inner.CreateNestedItemList(index);
        byte[][] paths = new byte[group.Count][];
        for (int j = 0; j < group.Count; j++)
            paths[j] = group.ReadContent(j).ToArray();
        return new PathGroup { Group = paths };
    }
}
