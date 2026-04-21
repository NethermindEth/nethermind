// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

namespace Nethermind.BalRecorder;

public class BalRecorderPlugin : INethermindPlugin
{
    public string Name => "BalRecorder";
    public string Description => "Records and replays block access lists as era files for prewarming benchmarks";
    public string Author => "Nethermind";
    public bool Enabled => true;
    public IModule? Module => new BalRecorderModule();
}

public class BalRecorderModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(ctx =>
            {
                IBalRecorderConfig config = ctx.Resolve<IBalRecorderConfig>();
                if (!config.ReplayEnabled && !config.RecordingEnabled)
                    return (IRecordedBalStore)NullRecordedBalStore.Instance;
                string directory = config.Path.GetApplicationResourcePath(
                    ctx.Resolve<IInitConfig>().BaseDbPath);
                return (IRecordedBalStore)new RecordedBalStore(directory, config);
            })
            .As<IRecordedBalStore>()
            .SingleInstance();
        builder.RegisterDecorator<BalReplayBranchProcessor, Nethermind.Consensus.Processing.IBranchProcessor>();
        builder.RegisterDecorator<BalRecordingBlockProcessor, Nethermind.Consensus.Processing.IBlockProcessor>();
        builder.RegisterDecorator<BalTracingTransactionsExecutor, Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor>();
    }
}
