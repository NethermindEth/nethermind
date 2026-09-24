// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Network;
using NSubstitute;

namespace Nethermind.BeaconChain.Test;

/// <summary>The production <see cref="BeaconChainModule"/> plus stand-ins for the services the host registers.</summary>
/// <remarks>A new module registration that needs a host service gets its stand-in here, not in each test file. Later registrations on the returned builder override these.</remarks>
internal static class BeaconChainTestContainer
{
    public static ContainerBuilder Builder(
        ulong chainId = BlockchainIds.Mainnet,
        ILogManager? logManager = null,
        IEngineRpcModule? engine = null,
        IBeaconChainConfig? config = null)
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>(); // registered by NetworkModule in production
        ipResolver.Resolve(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback)));
        ISpecProvider specProvider = Substitute.For<ISpecProvider>(); // registered by NethermindModule in production
        specProvider.ChainId.Returns(chainId);
        return new ContainerBuilder()
            .AddModule(new BeaconChainModule())
            .AddSingleton(config ?? new BeaconChainConfig())
            .AddSingleton(logManager ?? LimboLogs.Instance)
            .AddSingleton(engine ?? Substitute.For<IEngineRpcModule>()) // registered by MergePlugin in production
            .AddSingleton<ITimestamper>(Timestamper.Default) // registered by NethermindModule in production
            .AddSingleton(ipResolver)
            .AddSingleton(specProvider)
            .AddSingleton<IDbFactory, MemDbFactory>();
    }
}
