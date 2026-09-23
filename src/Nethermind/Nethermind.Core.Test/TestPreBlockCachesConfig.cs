// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Core.Test;

public static class TestPreBlockCachesConfig
{
    /// <summary>
    /// Caches sized for tests, which touch a handful of keys. The production sizes allocate 43 MiB per instance,
    /// which is wasted on a fixture that builds one per test case and runs them in parallel.
    /// </summary>
    public static PreBlockCachesConfig Small { get; } = new()
    {
        StateCacheSetsBits = SeqlockCache<AddressAsKey, Account>.DefaultSetsBits,
        StorageCacheSetsBits = SeqlockCache<StorageCell, UInt256>.DefaultSetsBits
    };
}
