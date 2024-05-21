// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Trie;

public class NodeStorageFactory : INodeStorageFactory
{
    private readonly INodeStorage.KeyScheme _preferredKeyScheme;
    private INodeStorage.KeyScheme? _currentKeyScheme;
    private readonly ILogger _logger;

    public NodeStorageFactory(INodeStorage.KeyScheme preferredKeyScheme, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _preferredKeyScheme = preferredKeyScheme;
        _currentKeyScheme = null;
    }

    public INodeStorage.KeyScheme? CurrentKeyScheme => _currentKeyScheme!;

    public void DetectCurrentKeySchemeFrom(IDb mainStateDb)
    {
        _currentKeyScheme = DetectKeyScheme(mainStateDb);
        if (_currentKeyScheme is null)
        {
            _logger.Info("No current state db key scheme.");
        }
        else
        {
            _logger.Info($"Detected {_currentKeyScheme} key scheme.");
        }
    }

    public INodeStorage WrapKeyValueStore(IKeyValueStore keyValueStore, bool forceUsePreferredKeyScheme = false)
    {
        bool preferredNotCurrent = _preferredKeyScheme != INodeStorage.KeyScheme.Current;
        INodeStorage.KeyScheme effectiveKeyScheme = forceUsePreferredKeyScheme && preferredNotCurrent
            ? _preferredKeyScheme
            : _currentKeyScheme ?? (preferredNotCurrent ? _preferredKeyScheme : INodeStorage.KeyScheme.HalfPath);

        bool requirePath = effectiveKeyScheme == INodeStorage.KeyScheme.HalfPath ||
                           _currentKeyScheme == INodeStorage.KeyScheme.HalfPath ||
                           _preferredKeyScheme == INodeStorage.KeyScheme.HalfPath;

        return new NodeStorage(
            keyValueStore,
            effectiveKeyScheme,
            requirePath
        );
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
