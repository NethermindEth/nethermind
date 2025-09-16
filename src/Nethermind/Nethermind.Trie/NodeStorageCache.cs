// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private ConcurrentDictionary<NodeKey, byte[]> _cache = new();

    private bool _enabled = false;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public byte[]? GetOrAdd(NodeKey nodeKey, Func<NodeKey, byte[]> tryLoadRlp)
    {
        if (!_enabled)
        {
            return tryLoadRlp(nodeKey);
        }
        return _cache.GetOrAdd(nodeKey, tryLoadRlp);
    }

    public bool ClearCaches()
    {
        return _cache.NoResizeClear();
    }
}
