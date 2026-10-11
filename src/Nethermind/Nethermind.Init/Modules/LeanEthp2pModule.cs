// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Init.Steps;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

namespace Nethermind.Init.Modules;

/// <summary>Checks the EIP-8437 binding configuration and registers the ethp2p profile when it is a supported binding.</summary>
/// <remarks>
/// A network designates one common binding that every participating node supports; others are optional. Authenticated
/// broadcast additionally needs an <see cref="ILeanBroadcastProfile"/> registered by the network's configuration.
/// </remarks>
public class LeanEthp2pModule(INetworkConfig networkConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        Validate(networkConfig);
        if ((networkConfig.LeanBindings & LeanBinding.Ethp2p) == 0
            || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())) return;
        builder
            .AddSingleton<LeanEthp2pHost>()
            .AddDecorator<INodeRecordProvider, LeanEthp2pNodeRecordProvider>()
            .AddStep(typeof(StartLeanEthp2p));
    }

    /// <exception cref="InvalidConfigurationException">The common binding is not exactly one binding this node supports.</exception>
    public static void Validate(INetworkConfig config)
    {
        LeanBinding common = config.LeanCommonBinding;
        if (common is not (LeanBinding.Rlpx or LeanBinding.Ethp2p))
            throw new InvalidConfigurationException($"{nameof(INetworkConfig.LeanCommonBinding)} must be Rlpx or Ethp2p", ExitCodes.ConflictingConfigurations);
        if ((config.LeanBindings & common) == 0)
            throw new InvalidConfigurationException($"{nameof(INetworkConfig.LeanBindings)} must include the common binding {common}", ExitCodes.ConflictingConfigurations);
    }
}
