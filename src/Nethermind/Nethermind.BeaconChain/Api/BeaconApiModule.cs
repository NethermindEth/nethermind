// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;

namespace Nethermind.BeaconChain.Api;

/// <summary>
/// Registration for the Beacon API, kept apart from <see cref="BeaconChainModule"/> so the API
/// carries its own config gate (<see cref="IBeaconApiConfig.Enabled"/>) independent of
/// <see cref="IBeaconChainConfig.Enabled"/>.
/// </summary>
public class BeaconApiModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<HeadSnapshotHolder>()
            .AddSingleton<EngineAvailability>()
            .AddDecorator<IEngineDriver, ObservedEngineDriver>()
            .AddStep(typeof(StartBeaconApi));

        builder.RegisterType<BeaconApiHost>().SingleInstance()
            .OnActivated(e => e.Instance.EngineAvailability = e.Context.Resolve<EngineAvailability>());
    }
}
