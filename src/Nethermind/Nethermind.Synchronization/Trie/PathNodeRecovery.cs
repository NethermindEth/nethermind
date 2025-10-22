// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Tasks;
using Nethermind.Core.Utils;
using Nethermind.Logging;
using Nethermind.State.Healing;
using Nethermind.Trie;

namespace Nethermind.Synchronization.Trie;

public class PathNodeRecovery(NodeDataRecovery nodeDataRecovery, SnapRangeRecovery snapRangeRecovery, ILogManager logManager) : IPathRecovery
{
    private readonly ILogger _logger = logManager.GetClassLogger<PathNodeRecovery>();

    public async Task<IOwnedReadOnlyList<(TreePath, byte[])>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken = default)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource(TimeSpan.FromSeconds(3));

        if (_logger.IsDebug) _logger.Debug($"Repairing path {address ?? Hash256.Zero}:{fullPath} starting from {startingPath}");

        try
        {
            IOwnedReadOnlyList<(TreePath, byte[])>? res = await Wait.AnyWhere(
                res => res != null,
                nodeDataRecovery.Recover(rootHash, address, startingPath, startingNodeHash, fullPath, cts.Token),
                snapRangeRecovery.Recover(rootHash, address, startingPath, startingNodeHash, fullPath, cts.Token)
            );

            if (res is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to recover path {address ?? Hash256.Zero}:{fullPath}");
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Fetched {res.Count} nodes to repair path {address ?? Hash256.Zero}:{fullPath} starting from {startingPath}");
            }

            return res;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
