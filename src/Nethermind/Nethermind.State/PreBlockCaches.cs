// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Trie;

namespace Nethermind.State;

public class PreBlockCaches
{
    public ConcurrentDictionary<StorageCell, byte[]> StorageCache { get; } = new(Environment.ProcessorCount * 2, 4096 * 4);
    public ConcurrentDictionary<AddressAsKey, Account> StateCache { get; } = new(Environment.ProcessorCount * 2, 4096 * 4);
    public ConcurrentDictionary<NodeKey, byte[]?> RlpCache { get; } = new(Environment.ProcessorCount * 2, 4096 * 4);

    public bool IsDirty => StorageCache.Count > 0 || StateCache.Count > 0 || RlpCache.Count > 0;

    public void Clear()
    {
        StorageCache.Clear();
        StateCache.Clear();
        RlpCache.Clear();
    }
}
