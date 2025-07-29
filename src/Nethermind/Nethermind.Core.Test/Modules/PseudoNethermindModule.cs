// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Reflection;

using Autofac;

using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.TxPool;

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

            .AddModule(new PsudoNetworkModule())
            .AddModule(new BlockTreeModule())
            .AddModule(new BlockProcessingModule())

            // Environments
            .AddSingleton<DisposableStack>()
            .AddSingleton<ITimerFactory, TimerFactory>()
            .AddSingleton<IBackgroundTaskScheduler, MainBlockProcessingContext>((blockProcessingContext) => new BackgroundTaskScheduler(
                blockProcessingContext.BlockProcessor,
                new ChainHeadInfoMock(),
                initConfig.BackgroundTaskConcurrency,
                initConfig.BackgroundTaskMaxNumber,
                logManager))
            .AddSingleton<IFileSystem>(new FileSystem())
            .AddSingleton<IDbProvider>(new DbProvider())
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))

            // Crypto
            .AddSingleton<ICryptoRandom>(new CryptoRandom())
            .AddSingleton<IEthereumEcdsa, ISpecProvider>((specProvider) => new EthereumEcdsa(specProvider.ChainId))
            .Bind<IEcdsa, IEthereumEcdsa>()
            .AddSingleton<IEciesCipher, EciesCipher>()
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

    private class ChainHeadInfoMock : IChainHeadInfoProvider
    {
        public IChainHeadSpecProvider SpecProvider { get; } = null!;
        public ICodeInfoRepository CodeInfoRepository { get; } = null!;
        public IReadOnlyStateProvider ReadOnlyStateProvider { get; } = null!;
        public long HeadNumber { get; }
        public long? BlockGasLimit { get; }
        public UInt256 CurrentBaseFee { get; }
        public UInt256 CurrentFeePerBlobGas { get; }
        public bool IsSyncing { get => false; }
        public bool IsProcessingBlock { get; }

        public event EventHandler<BlockReplacementEventArgs> HeadChanged { add { } remove { } }
    }
}
