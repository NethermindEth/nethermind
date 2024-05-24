// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.State;

public class PreBlockCaches
{
    public NonBlocking.ConcurrentDictionary<StorageCell, byte[]> StorageCache { get; } = new(Environment.ProcessorCount, 4096);
    public NonBlocking.ConcurrentDictionary<AddressAsKey, Account> StateCache { get; } = new(Environment.ProcessorCount, 4096);

    public bool IsDirty => StorageCache.Count > 0 || StateCache.Count > 0;

    public void Clear()
    {
        StorageCache.Clear();
        StateCache.Clear();
    }
}

