// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Reflection;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
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
    protected override void Load(ContainerBuilder builder)
    {
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();

        base.Load(builder);
        builder
            .AddModule(new NethermindModule(spec, configProvider, logManager))
            .AddModule(new PseudoNetworkModule())
            .AddModule(new BlockTreeModule())
            .AddModule(new TestBlockProcessingModule())

            // Environments
            .AddSingleton<ITimerFactory, TimerFactory>()
            .AddSingleton<IBackgroundTaskScheduler, IMainProcessingContext, IChainHeadInfoProvider>((blockProcessingContext, chainHeadInfoProvider) => new BackgroundTaskScheduler(
                blockProcessingContext.BranchProcessor,
                chainHeadInfoProvider,
                initConfig.BackgroundTaskConcurrency,
                initConfig.BackgroundTaskMaxNumber,
                logManager))
            .AddSingleton<IFileSystem>(new FileSystem())
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<IJsonSerializer, EthereumJsonSerializer>()

            // Crypto
            .AddSingleton<ICryptoRandom>(new CryptoRandom())

            .AddSingleton<ISignerStore>(NullSigner.Instance)
            .AddSingleton<IKeyStore>(Substitute.For<IKeyStore>())
            .AddSingleton<IWallet, DevWallet>()
            .AddSingleton<ITxSender>(Substitute.For<ITxSender>())

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
}
