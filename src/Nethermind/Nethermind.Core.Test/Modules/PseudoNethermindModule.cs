// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
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
        initConfig.AutoDump = DumpOptions.None;

        configProvider.GetConfig<IFlatDbConfig>().EnableLongFinality = false;

        base.Load(builder);
        builder
            .AddModule(new NethermindModule(spec, configProvider, logManager))
            .AddModule(new PseudoNetworkModule())
            .AddModule(new TestBlockProcessingModule())

            // Environments
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<IJsonSerializer, EthereumJsonSerializer>()

            // Crypto
            .AddSingleton<ISignerStore>(NullSigner.Instance)
            .AddSingleton<IKeyStore>(NullKeyStore.Instance)
            .AddSingleton<IWallet, DevWallet>()
            .AddSingleton<ITxSender>(NullTxSender.Instance)

            // FlatDb uses SnapshotableMemColumnsDb for fast O(1) MVCC snapshots instead of slow O(n) full copies
            .AddSingleton<IColumnsDb<FlatDbColumns>>((_) => new SnapshotableMemColumnsDb<FlatDbColumns>(neverPrune: true))
            .AddDecorator<IFlatDbManager, FlatDbManagerTestCompat>()
            .Intercept<IFlatDbConfig>((flatDbConfig, ctx) =>
            {
                // Dont want to make it very slow
                flatDbConfig.TrieWarmerWorkerCount = 0;
                flatDbConfig.WarmReadConcurrency = 2;
                // Fresh flat + FastSync without SnapSync is refused in production. Tests that still
                // set that combo are not syncing flat state, so they stay on patricia. An on-disk
                // flat DB (an sst file) stays enabled and the policy warns instead.
                if (flatDbConfig.Enabled
                    && !flatDbConfig.ImportFromPruningTrieState
                    && ctx.Resolve<ISyncConfig>() is { FastSync: true, SnapSync: false }
                    && !FlatDirectoryHasSst(ctx))
                {
                    flatDbConfig.Enabled = false;
                }
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

    private static bool FlatDirectoryHasSst(IComponentContext ctx)
    {
        IInitConfig initConfig = ctx.Resolve<IInitConfig>();
        if (string.IsNullOrEmpty(initConfig.BaseDbPath))
            return false;

        IFileSystem fileSystem = ctx.Resolve<IFileSystem>();
        string flatPath = Path.Combine(initConfig.BaseDbPath, DbNames.Flat);
        return fileSystem.Directory.Exists(flatPath)
            && fileSystem.Directory.EnumerateFiles(flatPath, "*.sst").Any();
    }
}
