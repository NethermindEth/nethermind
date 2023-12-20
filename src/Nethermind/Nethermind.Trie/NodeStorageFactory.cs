// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.Trie;

public class NodeStorageFactory : INodeStorageFactory
{
    private readonly INodeStorage.KeyScheme _preferredKeyScheme;
    private INodeStorage.KeyScheme? _currentKeyScheme;

    public NodeStorageFactory(INodeStorage.KeyScheme preferredKeyScheme)
    {
        _preferredKeyScheme = preferredKeyScheme;
        _currentKeyScheme = null;
    }

    public void DetectCurrentKeySchemeFrom(IDb mainStateDb)
    {
        _currentKeyScheme = DetectKeyScheme(mainStateDb);
    }

    public INodeStorage WrapKeyValueStore(IKeyValueStore keyValueStore, bool forceUsePreferredKeyScheme = false)
    {
        return new NodeStorage(keyValueStore, _currentKeyScheme ?? _preferredKeyScheme);
    }

    private static INodeStorage.KeyScheme? DetectKeyScheme(IDb db)
    {
        // Sample 20 keys
        // If most of them have length == 32, they are hash db.
        // Otherwise, its probably halfpath

        int total = 0;
        int keyOfLength32 = 0;
        foreach (KeyValuePair<byte[], byte[]?> keyValuePair in db.GetAll().Take(20))
        {
            total++;
            if (keyValuePair.Key.Length == 32)
            {
                keyOfLength32++;
            }
        }

        // Eh.. can't decide.
        if (total < 20) return null;

        return keyOfLength32 > 10 ? INodeStorage.KeyScheme.Hash : INodeStorage.KeyScheme.HalfPath;
    }
}
