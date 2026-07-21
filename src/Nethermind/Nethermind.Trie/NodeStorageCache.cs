// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private const int MissLockCount = 256;

    private readonly SeqlockCache<NodeKey, byte[]> _cache = new();
    private readonly Lock[] _missLocks = CreateMissLocks();

    private volatile bool _enabled = false;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public byte[]? GetOrAdd(in NodeKey nodeKey, SeqlockCache<NodeKey, byte[]>.ValueFactory tryLoadRlp)
    {
        if (!_enabled)
        {
            return tryLoadRlp(in nodeKey);
        }

        if (_cache.TryGetValue(in nodeKey, out byte[]? value))
        {
            return value;
        }

        return GetOrAddMiss(in nodeKey, tryLoadRlp);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[]? GetOrAddMiss(in NodeKey nodeKey, SeqlockCache<NodeKey, byte[]>.ValueFactory tryLoadRlp)
    {
        Lock missLock = _missLocks[(int)((ulong)nodeKey.GetHashCode64() & (MissLockCount - 1))];
        lock (missLock)
        {
            if (!_enabled)
            {
                return tryLoadRlp(in nodeKey);
            }

            if (_cache.TryGetValue(in nodeKey, out byte[]? value))
            {
                return value;
            }

            value = tryLoadRlp(in nodeKey);
            _cache.Set(in nodeKey, value);
            return value;
        }
    }

    public bool ClearCaches()
    {
        bool wasEnabled = _enabled;
        _enabled = false;
        _cache.Clear();
        return wasEnabled;
    }

    private static Lock[] CreateMissLocks()
    {
        Lock[] missLocks = new Lock[MissLockCount];
        for (int i = 0; i < missLocks.Length; i++)
        {
            missLocks[i] = new Lock();
        }

        return missLocks;
    }
}
