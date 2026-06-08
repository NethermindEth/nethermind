// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db.Blooms
{
    public interface IBloomStorage : IDisposable
    {
        ulong MinBlockNumber { get; }

        void Store(IReadOnlyList<(ulong BlockNumber, Bloom Bloom)> blooms);
        void Store(ulong blockNumber, Bloom bloom);

        void Migrate(IEnumerable<BlockHeader> blockHeaders);

        IBloomEnumeration GetBlooms(ulong fromBlock, ulong toBlock);

        bool ContainsRange(in ulong fromBlockNumber, in ulong toBlockNumber);

        public bool NeedsMigration => MinBlockNumber != 0;

        IEnumerable<Average> Averages { get; }

        ulong MigratedBlockNumber { get; }
    }
}
