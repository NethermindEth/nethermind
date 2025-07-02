// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core.Exceptions;

namespace Nethermind.Runner.Ethereum.Modules;

public class NethermindInvariantChecks : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .RegisterBuildCallback(RunChecks);
    }

    private void RunChecks(ILifetimeScope ctx)
    {
        IConsensusPlugin[] consensusPlugins = ctx.Resolve<IConsensusPlugin[]>();
        if (consensusPlugins.Length != 1)
        {
            throw new InvalidConfigurationException("There should be exactly one consensus plugin.", -1);
        }

        IConsensusPlugin consensusPlugin = consensusPlugins[0];
        INethermindApi nethermindApi = ctx.Resolve<INethermindApi>();

        if (nethermindApi.GetType() != consensusPlugin.ApiType)
        {
            throw new InvalidConfigurationException($"Incorrect INethermindApi type. Expected {consensusPlugin.ApiType}, got {nethermindApi.GetType()}", -1);
        }
    }
}
