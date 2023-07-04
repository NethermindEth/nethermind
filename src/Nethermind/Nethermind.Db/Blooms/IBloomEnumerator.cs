// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db.Blooms
{
    public interface IBloomEnumeration : IEnumerable<Core.Bloom>
    {
        bool TryGetBlockNumber(out long blockNumber);
        (long FromBlock, long ToBlock) CurrentIndices { get; }
    }
}
