// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Verkle.Tree.Sync;

public class GetLeafNodesRequest
{
    public Hash256 RootHash { get; set; }

    public byte[][] LeafNodePaths { get; set; }
}
