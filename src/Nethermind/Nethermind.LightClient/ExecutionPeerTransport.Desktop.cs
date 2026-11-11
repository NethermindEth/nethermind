// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.LightClient;

internal sealed partial class ExecutionPeerTransport
{
    private static Module CreateModule(ChainSpec chainSpec, IConfigProvider config, ILogManager logs) =>
        new NethermindModule(chainSpec, config, logs);
}
