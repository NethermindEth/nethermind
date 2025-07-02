// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Facade;

public static class BlockchainBridgeExtensions
{
    public static bool HasStateForBlock(this IBlockchainBridge blockchainBridge, BlockHeader header) =>
        blockchainBridge.HasStateForRoot(header);
}
