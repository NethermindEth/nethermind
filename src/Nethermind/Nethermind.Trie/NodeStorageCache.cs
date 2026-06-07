// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private const int LockCount = 1 << 14;
    private const int LockMask = LockCount - 1;

    private readonly SeqlockCache<NodeKey, byte[]> _cache = new();
    private readonly Lock[] _locks = CreateLocks();

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

        Lock gate = GetLock(in nodeKey);
        using (gate.EnterScope())
        {
            if (_cache.TryGetValue(in nodeKey, out value))
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Lock GetLock(in NodeKey nodeKey) => _locks[(int)nodeKey.GetHashCode64() & LockMask];

    private static Lock[] CreateLocks()
    {
        Lock[] locks = new Lock[LockCount];
        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = new Lock();
        }

        return locks;
    }
}
