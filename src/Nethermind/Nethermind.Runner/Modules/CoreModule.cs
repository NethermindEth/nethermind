// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core.Container;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Module = Autofac.Module;

namespace Nethermind.Runner.Modules;

/// <summary>
/// CoreModule should be something Nethermind specific
/// </summary>
public class CoreModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IGasLimitCalculator, FollowOtherMiners>()
            .AddSingleton<INethermindApi, NethermindApi>()
            .AddInstance<IPoSSwitcher>(NoPoS.Instance)
            .Bind<IEthereumEcdsa, IEcdsa>()
            .Bind<IBlockTree, IBlockFinder>();

        builder.Register(ctx =>
            {
                var nodeStorageFactory = ctx.Resolve<INodeStorageFactory>();
                var stateDb = ctx.Resolve<IDbProvider>().StateDb;
                return nodeStorageFactory.WrapKeyValueStore(stateDb);
            })
            .As<INodeStorage>();

        builder.Register(ctx => ctx.Resolve<ITrieStore>().AsReadOnly())
            .As<IReadOnlyTrieStore>();

        builder.RegisterSource(new FallbackToFieldFromApi<INethermindApi>());
    }
}
