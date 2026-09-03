// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
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
using Nethermind.TxPool;
using Nethermind.Wallet;
using Module = Autofac.Module;

namespace Nethermind.Core.Test.Modules;

/// <summary>Creates a test Nethermind configuration; requires <see cref="TestEnvironmentModule"/>.</summary>
public class PseudoNethermindModule(ChainSpec spec, IConfigProvider configProvider, ILogManager logManager) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
        initConfig.AutoDump = DumpOptions.None;

        configProvider.GetConfig<IFlatDbConfig>().EnableLongFinality = false;

        base.Load(builder);
        builder
            .AddModule(new NethermindModule(spec, configProvider, logManager))
            .AddModule(new PseudoNetworkModule())
            .AddModule(new TestBlockProcessingModule())

            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<IJsonSerializer, EthereumJsonSerializer>()

            .AddSingleton<ISignerStore>(NullSigner.Instance)
            .AddSingleton<IKeyStore>(NullKeyStore.Instance)
            .AddSingleton<IWallet, DevWallet>()
            .AddSingleton<ITxSender>(NullTxSender.Instance)

            .AddSingleton<IColumnsDb<FlatDbColumns>>((_) => new SnapshotableMemColumnsDb<FlatDbColumns>(neverPrune: true))
            .AddDecorator<IFlatDbManager, FlatDbManagerTestCompat>()
            .Intercept<IFlatDbConfig>((flatDbConfig) =>
            {
                flatDbConfig.TrieWarmerWorkerCount = 0;
                flatDbConfig.WarmReadConcurrency = 2;
            })

            .AddSingleton<IJsonRpcService, JsonRpcService>()
            ;


        // Network message decoding relies on globally registered RLP decoders.
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
