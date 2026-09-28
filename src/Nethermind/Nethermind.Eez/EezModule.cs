// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.TxPool;

namespace Nethermind.Eez;

public class EezModule(IEezConfig config) : Module
{
    private const string L1RpcClientKey = "Eez.L1";
    private const string SequencerRpcClientKey = "Eez.Sequencer";

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddDecorator<ISpecProvider, EezSpecProvider>()
            .AddScoped<ITransactionProcessor, EezTransactionProcessor>()
            .AddSingleton<ITransactionProcessorFactory, EezTransactionProcessorFactory>()
            .AddSingleton(EezExecutionRequests.Options)
            .AddSingleton<IBlockValidator, EezBlockValidator>()
            .AddKeyedSingleton<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey,
                static ctx => new EezSpecChangeTxValidator(ctx.Resolve<ISpecProvider>().ChainId));

        if (config.FollowerEnabled)
        {
            LoadFollower(builder);
        }
    }

    private void LoadFollower(ContainerBuilder builder)
    {
        builder
            .AddKeyedSingleton<IJsonRpcClient>(L1RpcClientKey, static ctx =>
            {
                IEezConfig eez = ctx.Resolve<IEezConfig>();
                return new BasicJsonRpcClient(new Uri(eez.L1RpcUrl!), ctx.Resolve<IJsonSerializer>(), ctx.Resolve<ILogManager>());
            })
            .AddSingleton<IEezL1Api>(static ctx => new EezL1Api(ctx.ResolveKeyed<IJsonRpcClient>(L1RpcClientKey)))
            .AddSingleton<EezSettlementContext, ISpecProvider>(specProvider =>
                new EezSettlementContext(config.RollupId, specProvider.ChainId, Address.Zero, default, config.L2BlockTimeSeconds, config.L2GasLimit))
            .AddSingleton<DerivedBlockBuilder>()
            .AddSingleton<IDerivedBlockExecutor, DerivedBlockExecutor>()
            .AddSingleton<IEezL2Engine, EezL2Engine>()
            .AddSingleton<IL1BatchScanner, IEezL1Api, ILogManager>((l1, logManager) =>
                new L1BatchScanner(l1, new Address(config.RegistryAddress!), config.RollupId, logManager))
            .AddSingleton<ResumePointFinder, IEezL1Api, IBlockTree>((l1, blockTree) =>
                new ResumePointFinder(l1, blockTree, new Address(config.RegistryAddress!), config.RollupId, config.RegistryDeployBlock, config.L1LogScanBlocks))
            .AddSingleton<IBatchReconciler, BatchReconciler>()
            .AddSingleton<IUnsafeHeadSource>(NullUnsafeHeadSource.Instance)
            .AddSingleton<EezFollower>()
            .AddStep(typeof(StartEezFollower));

        if (!string.IsNullOrEmpty(config.SequencerRpcUrl))
        {
            builder
                .AddKeyedSingleton<IJsonRpcClient>(SequencerRpcClientKey, static ctx =>
                {
                    IEezConfig eez = ctx.Resolve<IEezConfig>();
                    return new BasicJsonRpcClient(new Uri(eez.SequencerRpcUrl!), ctx.Resolve<IJsonSerializer>(), ctx.Resolve<ILogManager>());
                })
                .AddSingleton<IEezSequencerApi>(static ctx => new EezSequencerApi(ctx.ResolveKeyed<IJsonRpcClient>(SequencerRpcClientKey)))
                .AddSingleton<IUnsafeHeadSource, UnsafeHeadFollower>();
        }
    }
}
