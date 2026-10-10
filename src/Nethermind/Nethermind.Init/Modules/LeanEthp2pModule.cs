// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

namespace Nethermind.Init.Modules;

/// <summary>Registers the optional EIP-8437 ethp2p QUIC binding of <c>lean/1</c> when <see cref="INetworkConfig.LeanEthp2pEnabled"/> is set.</summary>
public class LeanEthp2pModule(INetworkConfig networkConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (!networkConfig.LeanEthp2pEnabled || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())) return;
        builder
            .AddSingleton<LeanEthp2pHost>()
            .AddDecorator<INodeRecordProvider, LeanEthp2pNodeRecordProvider>()
            .AddStep(typeof(StartLeanEthp2p));
    }
}
