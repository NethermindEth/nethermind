// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Facade.Filters;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.TxPool;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializeBlockProducer))]
public class RegisterRpcModules(
    Lazy<IRpcModuleProvider> rpcModuleProvider, // Lazy because it could be disabled, in which case don't resolve it.

    IJsonRpcConfig jsonRpcConfig,
    ISubscriptionFactory subscriptionFactory,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IReceiptMonitor receiptMonitor,
    FilterStore filterStore,
    ITxPool txPool,
    IEthSyncingInfo ethSyncingInfo,
    IPeerPool peerPool,
    IRlpxHost rlpxHost,
    IBlockForRpcFactory blockForRpcFactory,
    ILogManager logManager,
    Lazy<IBlockProcessingQueue> blockProcessingQueue,
    ITimestamper timestamper,
    IBlocksConfig blocksConfig
) : IStep, IDisposable
{
    private OptimisticHeadSource? _optimisticHeads;

    public virtual Task Execute(CancellationToken cancellationToken)
    {
        if (!jsonRpcConfig.Enabled)
        {
            return Task.CompletedTask;
        }

        // lets add threads to support parallel eth_getLogs
        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        ThreadPool.SetMinThreads(workerThreads + Environment.ProcessorCount, completionPortThreads + Environment.ProcessorCount);

        RpcLimits.Init(jsonRpcConfig.RequestQueueLimit, jsonRpcConfig.MaxConcurrentSharedRequests);

        ILogger logger = logManager.GetClassLogger<RegisterRpcModules>();
        _optimisticHeads = CreateOptimisticHeadSource(logger);

        // Register the standard subscription types in the dictionary
        subscriptionFactory.RegisterStandardSubscriptions(
            blockTree,
            logManager,
            specProvider,
            receiptMonitor,
            filterStore,
            txPool,
            ethSyncingInfo,
            peerPool,
            rlpxHost,
            blockForRpcFactory,
            jsonRpcConfig,
            _optimisticHeads);

        // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
        if (logger.IsDebug) logger.Debug($"RPC modules  : {string.Join(", ", rpcModuleProvider.Value.Enabled.OrderBy(static x => x))}");
        ThisNodeInfo.AddInfo("RPC modules  :", $"{string.Join(", ", rpcModuleProvider.Value.Enabled.OrderBy(static x => x))}");

        return Task.CompletedTask;
    }

    /// <remarks>
    /// It only listens to the processing queue and does work for a block only while an optimistic newHeads subscription
    /// exists. Without a processing queue the node serves newHeads as before, and an <c>optimistic</c> request is ignored.
    /// </remarks>
    private OptimisticHeadSource? CreateOptimisticHeadSource(ILogger logger)
    {
        try
        {
            return new OptimisticHeadSource(blockProcessingQueue.Value, blockTree, timestamper, blocksConfig.SecondsPerSlot, logManager);
        }
        catch (Exception exception)
        {
            if (logger.IsWarn) logger.Warn($"Optimistic newHeads are unavailable: {exception.Message}");
            return null;
        }
    }

    public void Dispose() => _optimisticHeads?.Dispose();
}
