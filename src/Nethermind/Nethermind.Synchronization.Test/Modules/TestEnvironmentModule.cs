// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Synchronization.Test.Modules;

/// <summary>
/// Module that set up test environment which should make nethermind works without doing any actual IO.
/// </summary>
/// <param name="nodeKey"></param>
public class TestEnvironmentModule(PrivateKey nodeKey): Module
{
    public const string NodeKey = "NodeKey";

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IDbProvider>(TestMemDbProvider.Init())
            .AddSingleton<IFileStoreFactory>(new InMemoryDictionaryFileStoreFactory())

            .AddSingleton<BlockchainTestContext>()
            .AddSingleton<ISealer>(new NethDevSealEngine(nodeKey.Address))
            .AddSingleton<ITimestamper, ManualTimestamper>()

            .AddKeyedSingleton(NodeKey, nodeKey)

            .AddSingleton<IChainHeadInfoProvider, IComponentContext>((ctx) =>
            {
                ISpecProvider specProvider = ctx.Resolve<ISpecProvider>();
                IBlockTree blockTree = ctx.Resolve<IBlockTree>();
                IStateReader stateReader = ctx.Resolve<IStateReader>();
                ICodeInfoRepository codeInfoRepository = ctx.ResolveNamed<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState));
                return new ChainHeadInfoProvider(specProvider, blockTree, stateReader, codeInfoRepository)
                {
                    // It just need to override this.
                    HasSynced = true
                };
            });

    }
}
