// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Proving;
using Nethermind.Eez.Sequencer;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Init.Modules;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Eez;

public class EezModule(IEezConfig config) : Module
{
    private const string L1RpcClientKey = "Eez.L1";
    private const string SequencerRpcClientKey = "Eez.Sequencer";
    private const string BuilderRpcClientKey = "Eez.Builder";

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
            .AddSingleton<EezDriver>(static ctx => new EezDriver(ctx.Resolve<EezFollower>(),
                ctx.Resolve<IEezConfig>().SequencerEnabled ? ctx.Resolve<IEezSequencer>() : null, ctx.Resolve<IEezConfig>(), ctx.Resolve<ITimestamper>(),
                ctx.Resolve<IProcessExitSource>(), ctx.Resolve<ILogManager>()))
            .AddStep(typeof(StartEezDriver));

        if (config.SequencerEnabled)
        {
            LoadSequencer(builder);
        }

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

    private void LoadSequencer(ContainerBuilder builder)
    {
        RollupTiming timing = new(config.L1BlockTimeMs, (uint)(config.L2BlockTimeSeconds * 1000), config.ProofTimeMs, config.SubmissionSlackMs);
        Address beneficiary = string.IsNullOrEmpty(config.SequencerFeeRecipient) ? Address.Zero : new Address(config.SequencerFeeRecipient);
        builder
            .AddDatabase(WitnessStore.DbName, WitnessStore.DbName, "eezWitnesses")
            .AddSingleton<IWitnessStore, WitnessStore>()
            .AddSingleton<IWitnessRecorder, WitnessRecorder>()
            .AddSingleton<ILiveBlockProducer, IBlockProducerEnvFactory, ISpecProvider, EezSettlementContext>((envFactory, specProvider, context) =>
                new LiveBlockProducer(envFactory, specProvider, context, beneficiary))
            .AddSingleton<ISequencedBlocks, ILiveBlockProducer, IDerivedBlockExecutor, IEezL2Engine, IWitnessRecorder, ILogManager>(
                (live, derived, engine, witnesses, logManager) => new SequencedBlocks(live, derived, engine, witnesses, beneficiary, logManager))
            .AddSingleton<OptimisticLedger>()
            .AddSingleton<AttestationQuorum, ITimestamper, ILogManager>((clock, logManager) =>
                new AttestationQuorum(Attesters(), new ProveRetry(timing, clock), TimeSpan.FromMilliseconds(config.AttestationGraceMs), logManager))
            .AddSingleton<IQuorumRegistrationReader, IEezL1Api, ILogManager>((l1, logManager) =>
                new QuorumRegistrationReader(l1, new Address(config.RegistryAddress!), config.RollupId, logManager))
            .AddKeyedSingleton<IJsonRpcClient>(BuilderRpcClientKey, static ctx =>
            {
                IEezConfig eez = ctx.Resolve<IEezConfig>();
                return string.IsNullOrEmpty(eez.L1BuilderRpcUrl)
                    ? ctx.ResolveKeyed<IJsonRpcClient>(L1RpcClientKey)
                    : new BasicJsonRpcClient(new Uri(eez.L1BuilderRpcUrl), ctx.Resolve<IJsonSerializer>(), ctx.Resolve<ILogManager>());
            })
            .AddSingleton<IL1PostingApi>(static ctx => new L1PostingApi(ctx.ResolveKeyed<IJsonRpcClient>(L1RpcClientKey),
                ctx.ResolveKeyed<IJsonRpcClient>(BuilderRpcClientKey), ctx.Resolve<IJsonSerializer>()))
            .AddSingleton<IPostBatchPoster, IL1PostingApi, IEezL1Api, IWallet, ILogManager>((posting, l1, wallet, logManager) =>
                new PostBatchPoster(posting, l1, new WalletTxSigner(wallet, config.L1ChainId),
                    new PostingSettings(config.L1ChainId, new Address(config.RegistryAddress!), config.RollupId, new Address(config.PosterAddress!),
                        config.PostBatchPriorityFee, config.MaxPostBatchGas, !string.IsNullOrEmpty(config.L1BuilderRpcUrl)), logManager))
            .AddSingleton<ISyncSlotComposer>(ctx => new SyncSlotComposer(ctx.Resolve<IBlockTree>(), ctx.Resolve<ISequencedBlocks>(), ctx.Resolve<IWitnessRecorder>(),
                ctx.Resolve<OptimisticLedger>(), ctx.Resolve<AttestationQuorum>(), ctx.Resolve<IQuorumRegistrationReader>(), ctx.Resolve<IPostBatchPoster>(),
                ctx.Resolve<ISpecProvider>(), ctx.Resolve<EezSettlementContext>(), timing,
                new ComposerSettings(Math.Max(config.MaxBlocksPerBatch / timing.K, 1) * timing.K, config.MaxPostBatchGas), ctx.Resolve<ILogManager>()))
            .AddSingleton<IEezSequencer>(ctx => new EezSequencer(timing, ctx.Resolve<IBlockTree>().Genesis!.Timestamp, ctx.Resolve<ISequencedBlocks>(),
                ctx.Resolve<ISyncSlotComposer>(), ctx.Resolve<ITimestamper>(), config.MaxSpeculativeDepth, ctx.Resolve<ILogManager>()));
    }

    private RemoteAttester[] Attesters()
    {
        RemoteAttester[] attesters = new RemoteAttester[config.Provers.Length];
        for (int i = 0; i < attesters.Length; i++)
        {
            ProverEndpoint endpoint = ProverEndpoint.Parse(config.Provers[i])!;
            attesters[i] = new RemoteAttester(endpoint.Url, endpoint.ProofSystem, endpoint.Attester);
        }

        return attesters;
    }
}
