// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.State;

public class PreBlockCaches(string id)
{
    public string Id { get; } = id;
    public NonBlocking.ConcurrentDictionary<StorageCell, byte[]> StorageCache { get; } = new(Environment.ProcessorCount, 4096);
    public NonBlocking.ConcurrentDictionary<AddressAsKey, Account> StateCache { get; } = new(Environment.ProcessorCount, 4096);

    public void Clear()
    {
        StorageCache.Clear();
        StateCache.Clear();
    }
}

