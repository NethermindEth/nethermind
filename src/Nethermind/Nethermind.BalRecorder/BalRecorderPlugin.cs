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

public class BalRecorderModule : Module
{
    protected override void Load(ContainerBuilder builder) =>
        builder
            .AddSingleton<IRecordedBalStore, RecordedBalStore>()
            .AddSingleton<BalRecorderSpecSwitch>()
            .AddDecorator<ISpecProvider, BalRecorderSpecProvider>()
            .AddDecorator<IBlockValidator, BalRecordingBlockValidator>()
            .AddDecorator<IBlockProcessor, BalRecordingBlockProcessor>();
}
