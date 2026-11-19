// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Nethermind.BeaconChain.Spec;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.Spec;

/// <summary>Execution-layer fork schedules for resolving a <see cref="BeaconChainSpec"/> the way the plugin does.</summary>
internal static class ExecutionSchedules
{
    /// <summary>The built-in execution schedule of <paramref name="chainId"/>, or one that carries only the chain id.</summary>
    public static ISpecProvider ForChainId(ulong chainId)
    {
        switch (chainId)
        {
            case BlockchainIds.Mainnet: return MainnetSpecProvider.Instance;
            case BlockchainIds.Hoodi: return HoodiSpecProvider.Instance;
            case BlockchainIds.Sepolia: return SepoliaSpecProvider.Instance;
            case BlockchainIds.Gnosis: return GnosisSpecProvider.Instance;
            case BlockchainIds.Chiado: return ChiadoSpecProvider.Instance;
        }

        ISpecProvider unsupported = Substitute.For<ISpecProvider>();
        unsupported.ChainId.Returns(chainId);
        return unsupported;
    }

    public static BeaconChainSpec SpecForChainId(ulong chainId) => BeaconChainSpec.ForExecution(ForChainId(chainId));

    /// <summary>The execution schedule of <see cref="LoadChainspec"/>.</summary>
    public static ISpecProvider FromChainspec(string name, Action<JsonObject>? edit = null) => new ChainSpecBasedSpecProvider(LoadChainspec(name, edit));

    /// <summary>The shipped <c>chainspec/<paramref name="name"/>.json</c>, after <paramref name="edit"/> changes its JSON.</summary>
    public static ChainSpec LoadChainspec(string name, Action<JsonObject>? edit = null)
    {
        using Stream resource = typeof(IConfig).Assembly.GetManifestResourceStream($"Nethermind.Config.chainspec.{name}.json")
            ?? throw new ArgumentException($"No embedded chainspec named {name}.", nameof(name));
        JsonObject json = JsonNode.Parse(resource)!.AsObject();
        edit?.Invoke(json);
        using MemoryStream edited = new(Encoding.UTF8.GetBytes(json.ToJsonString()));
        return new AutoDetectingChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(edited);
    }
}
