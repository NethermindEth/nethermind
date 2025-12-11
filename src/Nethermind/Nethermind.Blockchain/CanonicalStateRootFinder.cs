// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat;

namespace Nethermind.Blockchain;

public class CanonicalStateRootFinder(IBlockTree blockTree): ICanonicalStateRootFinder
{
    public Hash256? GetCanonicalStateRootAtBlock(long blockNumber)
    {
        BlockHeader? header = blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        return header?.StateRoot;
    }
}
