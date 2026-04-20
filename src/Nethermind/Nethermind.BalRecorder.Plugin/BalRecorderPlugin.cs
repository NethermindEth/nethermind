// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

namespace Nethermind.BalRecorder;

public class BalRecorderPlugin : INethermindPlugin
{
    private const string EnvReplay = "BAL_REPLAY";
    private const string EnvRecord = "BAL_RECORD";
    private const string EnvPath = "BAL_PATH";
    private const string DefaultPath = "recordedBal";

    private static bool ReplayEnabled => Environment.GetEnvironmentVariable(EnvReplay) is "1" or "true";
    private static bool RecordingEnabled => Environment.GetEnvironmentVariable(EnvRecord) is "1" or "true";
    private static string BalPath => Environment.GetEnvironmentVariable(EnvPath) ?? DefaultPath;

    public string Name => "BalRecorder";
    public string Description => "Records and replays block access lists as era files for prewarming benchmarks";
    public string Author => "Nethermind";
    public bool Enabled => ReplayEnabled || RecordingEnabled;
    public IModule? Module => Enabled ? new BalRecorderModule(ReplayEnabled, RecordingEnabled, BalPath) : null;
}

public class BalRecorderModule(bool replayEnabled, bool recordingEnabled, string relativePath) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(ctx =>
            {
                string directory = relativePath.GetApplicationResourcePath(
                    ctx.Resolve<IInitConfig>().BaseDbPath);
                return new RecordedBalStore(directory, new BalRecorderConfig
                {
                    ReplayEnabled = replayEnabled,
                    RecordingEnabled = recordingEnabled,
                    Path = relativePath
                });
            })
            .As<IRecordedBalStore>()
            .SingleInstance();
        builder.RegisterDecorator<BalReplayBranchProcessor, Nethermind.Consensus.Processing.IBranchProcessor>();
        builder.RegisterDecorator<BalRecordingBlockProcessor, Nethermind.Consensus.Processing.IBlockProcessor>();
        builder.RegisterDecorator<BalTracingTransactionsExecutor, Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor>();
    }
}
