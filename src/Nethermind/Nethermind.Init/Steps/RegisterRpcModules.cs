// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
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
    ILogManager logManager
) : IStep
{
    public virtual Task Execute(CancellationToken cancellationToken)
    {
        if (!jsonRpcConfig.Enabled)
        {
            return Task.CompletedTask;
        }

        ILogger logger = logManager.GetClassLogger<RegisterRpcModules>();
        // Reserve default-pool workers for engine intake and following beside admitted CPU-bound RPCs.
        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        int minimumWorkers = Math.Max(workerThreads, RpcExecutionLimiter.GetConcurrency(jsonRpcConfig)) + Environment.ProcessorCount;
        if (!ThreadPool.SetMinThreads(minimumWorkers, completionPortThreads + Environment.ProcessorCount) && logger.IsWarn)
        {
            logger.Warn($"Unable to set the RPC worker minimum to {minimumWorkers}; check forced ThreadPool settings before qualifying following under load.");
        }

        RpcLimits.Init(jsonRpcConfig.RequestQueueLimit, jsonRpcConfig.MaxConcurrentSharedRequests);

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
            blockForRpcFactory);

        // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
        if (logger.IsDebug) logger.Debug($"RPC modules  : {string.Join(", ", rpcModuleProvider.Value.Enabled.OrderBy(static x => x))}");
        ThisNodeInfo.AddInfo("RPC modules  :", $"{string.Join(", ", rpcModuleProvider.Value.Enabled.OrderBy(static x => x))}");

        return Task.CompletedTask;
    }
}
