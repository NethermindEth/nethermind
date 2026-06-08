// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db.Blooms
{
    public interface IBloomEnumeration : IEnumerable<Core.Bloom>
    {
        bool TryGetBlockNumber(out ulong blockNumber);
        (ulong FromBlock, ulong ToBlock) CurrentIndices { get; }
    }
}
