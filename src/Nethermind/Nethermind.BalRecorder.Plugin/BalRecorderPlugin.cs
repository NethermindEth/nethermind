// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;

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

public class BalRecorderModule(bool replayEnabled, bool recordingEnabled, string directory) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterInstance(new RecordedBalStore(directory, replayEnabled, recordingEnabled))
               .As<IRecordedBalStore>()
               .SingleInstance();
        builder.RegisterDecorator<BalRecordingBlockProcessor, Nethermind.Consensus.Processing.IBlockProcessor>();
        builder.RegisterDecorator<BalTracingTransactionsExecutor, Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor>();
    }
}
