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

    public NodeStorageFactory(INodeStorage.KeyScheme preferredKeyScheme)
    {
        _preferredKeyScheme = preferredKeyScheme;
    }

    public INodeStorage WrapKeyValueStore(IKeyValueStore keyValueStore)
    {
        INodeStorage.KeyScheme keyScheme = DetectKeyScheme(keyValueStore) ?? _preferredKeyScheme;
        return new NodeStorage(keyValueStore, keyScheme);
    }

    private static INodeStorage.KeyScheme? DetectKeyScheme(IKeyValueStore keyValueStore)
    {
        if (keyValueStore is not IDb asDb) return null;

        // Sample 20 keys
        // If most of them have length == 32, they are hash db.
        // Otherwise, its probably halfpath

        int total = 0;
        int keyOfLength32 = 0;
        foreach (KeyValuePair<byte[], byte[]?> keyValuePair in asDb.GetAll().Take(20))
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
