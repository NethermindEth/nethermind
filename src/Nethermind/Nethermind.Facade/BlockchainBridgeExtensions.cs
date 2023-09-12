// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Trie;

namespace Nethermind.Facade;

public static class BlockchainBridgeExtensions
{
    public static bool HasStateForBlock(this IBlockchainBridge blockchainBridge, BlockHeader header)
    {
        RootCheckVisitor rootCheckVisitor = new();
        blockchainBridge.RunTreeVisitor(rootCheckVisitor, header.StateRoot!);
        return rootCheckVisitor.HasRoot;
    }
}
