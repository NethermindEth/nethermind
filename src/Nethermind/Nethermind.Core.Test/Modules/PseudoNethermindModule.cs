// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;
using Module = Autofac.Module;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Create a reasonably complete nethermind configuration.
/// It should not really have any test specific configuration which is set by `TestEnvironmentModule`.
/// May not work without `TestEnvironmentModule`.
/// </summary>
/// <param name="configProvider"></param>
/// <param name="spec"></param>
public class PseudoNethermindModule(ChainSpec spec, IConfigProvider configProvider, ILogManager logManager) : Module
{
    public static bool TestUseFlat = Environment.GetEnvironmentVariable("TEST_USE_FLAT") == "1";

    protected override void Load(ContainerBuilder builder)
    {
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
        if (TestUseFlat)
        {
            ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
            if (syncConfig.FastSync || syncConfig.SnapSync)
            {
                Assert.Ignore("Flat does not work with fast sync or snap sync");
            }
            configProvider.GetConfig<IFlatDbConfig>().Enabled = true;
        }

        base.Load(builder);
        builder
            .AddModule(new NethermindModule(spec, configProvider, logManager))
            .AddModule(new PseudoNetworkModule())
            .AddModule(new TestBlockProcessingModule())

            // Environments
            .AddSingleton<IBackgroundTaskScheduler, IMainProcessingContext, IChainHeadInfoProvider>((blockProcessingContext, chainHeadInfoProvider) => new BackgroundTaskScheduler(
                blockProcessingContext.BranchProcessor,
                chainHeadInfoProvider,
                initConfig.BackgroundTaskConcurrency,
                initConfig.BackgroundTaskMaxNumber,
                logManager))
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<IJsonSerializer, EthereumJsonSerializer>()

            // Crypto
            .AddSingleton<ISignerStore>(NullSigner.Instance)
            .AddSingleton<IKeyStore>(Substitute.For<IKeyStore>())
            .AddSingleton<IWallet, DevWallet>()
            .AddSingleton<ITxSender>(Substitute.For<ITxSender>())

            // Flatdb (if used) need a more complete memcolumndb implementation with snapshots and sorted view.
            .AddSingleton<IColumnsDb<FlatDbColumns>>((_) => new TestMemColumnsDb<FlatDbColumns>())
            .AddDecorator<IFlatDbManager, FlatDbManagerTestCompat>()
            .Intercept<IFlatDbConfig>((flatDbConfig) =>
            {
                // Dont want to make it very slow
                flatDbConfig.TrieWarmerWorkerCount = 2;
            })

            // Rpc
            .AddSingleton<IJsonRpcService, JsonRpcService>()
            ;


        // Yep... this global thing need to work.
        builder.RegisterBuildCallback((_) =>
        {
            Assembly? assembly = Assembly.GetAssembly(typeof(NetworkNodeDecoder));
            if (assembly is not null)
            {
                Rlp.RegisterDecoders(assembly, canOverrideExistingDecoders: true);
            }
        });
    }

    /// <summary>
    /// A LOT of test rely on the fact that trie store will assume state is available as long as the state root is
    /// empty tree even if the blocknumber is not -1. This does not work with flat. We will ignore it for now.
    /// </summary>
    /// <param name="flatDbManager"></param>
    private class FlatDbManagerTestCompat(IFlatDbManager flatDbManager) : IFlatDbManager
    {
        public SnapshotBundle GatherSnapshotBundle(StateId baseBlock, ResourcePool.Usage usage)
        {
            IgnoreOnInvalidState(baseBlock);
            return flatDbManager.GatherSnapshotBundle(baseBlock, usage);
        }

        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(StateId baseBlock)
        {
            IgnoreOnInvalidState(baseBlock);
            return flatDbManager.GatherReadOnlySnapshotBundle(baseBlock);
        }

        public bool HasStateForBlock(StateId stateId)
        {
            IgnoreOnInvalidState(stateId);
            return flatDbManager.HasStateForBlock(stateId);
        }

        public void IgnoreOnInvalidState(StateId stateId)
        {
            if (stateId.StateRoot == Keccak.EmptyTreeHash && stateId.BlockNumber != -1 &&
                !flatDbManager.HasStateForBlock(stateId))
            {
                Assert.Ignore("Incompatible test");
            }
        }

        public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);

        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) => flatDbManager.AddSnapshot(snapshot, transientResource);

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
        {
            add => flatDbManager.ReorgBoundaryReached += value;
            remove => flatDbManager.ReorgBoundaryReached -= value;
        }
    }

    public static void IgnoreIfRunningFlat()
    {
        if (TestUseFlat) Assert.Ignore("Does not work in flat");
    }
}
