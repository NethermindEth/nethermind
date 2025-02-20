// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PosForwardSyncHeaderProvider(
    IPosTransitionHook posTransitionHook,
    ISealValidator sealValidator,
    IBlockTree blockTree,
    IChainLevelHelper chainLevelHelper,
    ILogManager logManager
) : ForwardSyncHeaderProvider(
    posTransitionHook,
    sealValidator,
    blockTree,
    logManager)
{

    private ILogger _logger = logManager.GetClassLogger<PosForwardSyncHeaderProvider>();
    private readonly IBlockTree _blockTree = blockTree;

    public override Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(PeerInfo bestPeer, BlocksRequest blocksRequest, int maxHeader, CancellationToken cancellation)
    {
        if (_logger.IsDebug)
            _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

        int headersToRequest = Math.Min(maxHeader, bestPeer.MaxHeadersPerRequest());
        BlockHeader?[]? headers = chainLevelHelper.GetNextHeaders(headersToRequest, bestPeer.HeadNumber, blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0);
        if (headers is null || headers.Length <= 1)
        {
            if (_logger.IsTrace)
                _logger.Trace("Chain level helper got no headers suggestion");
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(null);
        }

        // Alternatively we can do this in BeaconHeadersSyncFeed, but this seems easier.
        ValidateSeals(headers!, cancellation);

        return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(headers.ToPooledList(headers.Length));
    }
}
