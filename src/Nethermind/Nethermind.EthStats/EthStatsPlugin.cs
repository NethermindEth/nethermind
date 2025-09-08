// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;

namespace Nethermind.EthStats;

public class EthStatsPlugin(IEthStatsConfig ethStatsConfig) : INethermindPlugin
{
    public bool Enabled => ethStatsConfig.Enabled;
    public string Name => "EthStats";
    public string Description => "Ethereum Statistics";
    public string Author => "Nethermind";
    public IModule Module => new EthStatsModule();
}

public class EthStatsModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder.AddStep(typeof(EthStatsStep));
}
