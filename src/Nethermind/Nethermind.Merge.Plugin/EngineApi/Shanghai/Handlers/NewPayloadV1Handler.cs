// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;
using Nethermind.Merge.Plugin.EngineApi.Paris.Handlers;
using Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai.Handlers;

/// <summary>
/// Defines a class that handles the execution payload according to the
/// <see href="https://eips.ethereum.org/EIPS/eip-3675">EIP-3675</see>.
/// <see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#engine_newpayloadv2">engine_newpayloadv2</see>.
/// </summary>
public abstract class NewPayloadV2AbstractHandler<TRequest, TResponse> : NewPayloadV1AbstractHandler<TRequest, TResponse>
    where TRequest : ExecutionPayloadV2
    where TResponse : PayloadStatusV1, IPayloadStatus<TResponse>, new()
{
    protected NewPayloadV2AbstractHandler(
        IBlockValidator blockValidator,
        IBlockTree blockTree,
        IInitConfig initConfig,
        ISyncConfig syncConfig,
        IPoSSwitcher poSSwitcher,
        IBeaconSyncStrategy beaconSyncStrategy,
        IBeaconPivot beaconPivot,
        IBlockCacheService blockCacheService,
        IBlockProcessingQueue processingQueue,
        IInvalidChainTracker invalidChainTracker,
        IMergeSyncController mergeSyncController,
        ISpecProvider specProvider,
        ILogManager logManager,
        TimeSpan? timeout = null,
        int cacheSize = 50)
    : base(
        blockValidator,
        blockTree,
        initConfig,
        syncConfig,
        poSSwitcher,
        beaconSyncStrategy,
        beaconPivot,
        blockCacheService,
        processingQueue,
        invalidChainTracker,
        mergeSyncController,
        specProvider,
        logManager,
        timeout,
        cacheSize)
    {
    }
}

public sealed class NewPayloadV2Handler : NewPayloadV2AbstractHandler<ExecutionPayloadV2, PayloadStatusV1>
{
    public NewPayloadV2Handler(
        IBlockValidator blockValidator,
        IBlockTree blockTree,
        IInitConfig initConfig,
        ISyncConfig syncConfig,
        IPoSSwitcher poSSwitcher,
        IBeaconSyncStrategy beaconSyncStrategy,
        IBeaconPivot beaconPivot,
        IBlockCacheService blockCacheService,
        IBlockProcessingQueue processingQueue,
        IInvalidChainTracker invalidChainTracker,
        IMergeSyncController mergeSyncController,
        ISpecProvider specProvider,
        ILogManager logManager,
        TimeSpan? timeout = null,
        int cacheSize = 50)
        : base(
            blockValidator,
            blockTree,
            initConfig,
            syncConfig,
            poSSwitcher,
            beaconSyncStrategy,
            beaconPivot,
            blockCacheService,
            processingQueue,
            invalidChainTracker,
            mergeSyncController,
            specProvider,
            logManager,
            timeout,
            cacheSize)
    {
    }
}
