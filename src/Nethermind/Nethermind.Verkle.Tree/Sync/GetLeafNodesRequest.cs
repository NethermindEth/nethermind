// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Verkle;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree.Sync;

public class GetLeafNodesRequest
{
    public Pedersen RootHash  { get; set; }

    public byte[][] LeafNodePaths { get; set; }
}
