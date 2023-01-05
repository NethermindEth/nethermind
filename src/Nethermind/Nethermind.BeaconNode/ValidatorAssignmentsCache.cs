// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Caching.Memory;

namespace Nethermind.BeaconNode
{
    public class ValidatorAssignmentsCache
    {
        public ValidatorAssignmentsCache()
        {
            Cache = new MemoryCache(new MemoryCacheOptions());
        }

        public IMemoryCache Cache { get; }
    }
}
