// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Modules;

public sealed class BlockTreeCloneHeaderSource(IBlockFinder blockFinder) : ICloneHeaderSource
{
    public ValueHash256? TryGetStateRoot(ulong block)
    {
        BlockHeader? header = blockFinder.FindHeader(block);
        return header?.StateRoot;
    }
}
