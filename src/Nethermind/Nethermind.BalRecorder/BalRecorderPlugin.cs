// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.BalRecorder;

public class BalRecorderPlugin(IBalRecorderConfig config) : INethermindPlugin
{
    public string Name => "BalRecorder";
    public string Description => "Records and replays block access lists as era files for prewarming benchmarks";
    public string Author => "Nethermind";
    public bool Enabled => config.RecordingEnabled || config.ReplayEnabled;
    public IModule? Module => new BalRecorderModule();
}

/// <remarks>
/// DEVELOPMENT / BENCHMARK USE ONLY. The decorators wrap every registered <see cref="IBranchProcessor"/>,
/// <see cref="IBlockProcessor"/>, <see cref="IBlockValidator"/>, and <see cref="ISpecProvider"/> — including
/// those used for simulation, eth_call, and the producer pipeline — so this plugin must not be enabled on
/// production nodes.
/// </remarks>
public class BalRecorderModule : Module
{
    protected override void Load(ContainerBuilder builder) =>
        builder
            .AddSingleton<IRecordedBalStore, RecordedBalStore>()
            .AddSingleton<BalRecorderSpecSwitch>()
            .AddDecorator<ISpecProvider, BalRecorderSpecProvider>()
            .AddDecorator<IBlockValidator, BalRecordingBlockValidator>()
            .AddDecorator<IBranchProcessor, BalRecordingBranchProcessor>()
            .AddDecorator<IBlockProcessor, BalRecordingBlockProcessor>();
}
