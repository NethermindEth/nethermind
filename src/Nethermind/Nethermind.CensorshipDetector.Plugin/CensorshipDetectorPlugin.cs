// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.CensorshipDetector.Plugin;

public class CensorshipDetectorPlugin(ICensorshipDetectorConfig config) : INethermindPlugin
{
    public string Name => "CensorshipDetector";
    public string Description => "Detects transaction censorship in processed blocks.";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;
    public IModule Module => new CensorshipDetectorModule();
}

public class CensorshipDetectorModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<CensorshipDetector>()
        .Bind<IBuilderOverridePolicy, CensorshipDetector>()
        .AddStep(typeof(InitializeCensorshipDetector));
}

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockchain)],
    dependents: [typeof(StartBlockProcessor)])]
public class InitializeCensorshipDetector(CensorshipDetector censorshipDetector) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = censorshipDetector;
        return Task.CompletedTask;
    }
}
