// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Optimism.CL;

// Stores only derived blocks. No p2p blocks
public interface IL2BlockTree
{
    L2Block? GetHighestBlock();
    L2Block? GetBlockByNumber(ulong number);
    void AddBlock(L2Block block);
}
