// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private readonly ConcurrentDictionary<NodeKey, byte[]> _cache = new();

    private bool _enabled;

    public byte[]? GetOrAdd(NodeKey nodeKey, Func<NodeKey, byte[]> tryLoadRlp) =>
        _enabled ? _cache.GetOrAdd(nodeKey, tryLoadRlp) : tryLoadRlp(nodeKey);

    public bool ClearCaches(bool enabled)
    {
        _enabled = enabled;
        return _cache.NoResizeClear();
    }
}
