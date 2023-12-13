// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db.Blooms
{
    public interface IBloomStorage : IDisposable
    {
        long MinBlockNumber { get; }

        void Store(long blockNumber, Core.Bloom bloom);

        void Migrate(IEnumerable<BlockHeader> blockHeaders);

        IBloomEnumeration GetBlooms(long fromBlock, long toBlock);

        bool ContainsRange(in long fromBlockNumber, in long toBlockNumber);

        public bool NeedsMigration => MinBlockNumber != 0;

        IEnumerable<Average> Averages { get; }

        long MigratedBlockNumber { get; }
    }
}
