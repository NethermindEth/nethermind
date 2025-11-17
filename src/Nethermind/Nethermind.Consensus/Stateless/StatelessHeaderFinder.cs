// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

public class StatelessHeaderFinder(Dictionary<Hash256, BlockHeader> hashToHeader) : IHeaderFinder
{
    public BlockHeader? Get(Hash256 blockHash, long? blockNumber = null)
    {
        hashToHeader.TryGetValue(blockHash, out BlockHeader? blockHeader);
        return blockHeader;
    }
}
