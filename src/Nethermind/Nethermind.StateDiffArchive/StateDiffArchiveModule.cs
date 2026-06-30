// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.StateDiffArchive.Recording;
using Nethermind.StateDiffArchive.Replay;
using Nethermind.StateDiffArchive.Storage;

namespace Nethermind.StateDiffArchive;

/// <summary>
/// Wires the state-diff archive. Recording and replay both intercept the main
/// <see cref="IWorldStateScopeProvider"/> (via an <see cref="IMainProcessingModule"/>, the only place that
/// registration is visible); replay additionally decorates <see cref="IBlockProcessor"/> to skip the EVM.
/// </summary>
public class StateDiffArchiveModule(IStateDiffArchiveConfig config) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        bool recording = config.RecordingEnabled;
        bool replay = config.ReplayEnabled && !recording;

        if (recording || replay)
        {
            builder.AddSingleton<StateDiffStore>();
            builder.AddSingleton<IMainProcessingModule, StateDiffArchiveMainProcessingModule>();
        }

        if (replay)
        {
            builder.AddSingleton<ReplayScopeTracker>();
            builder.AddDecorator<IBlockProcessor, ReplayBlockProcessor>();
        }
    }

    /// <summary>Runs inside the main block-processing scope, the only scope where the global <see cref="IWorldStateScopeProvider"/> is registered and thus decoratable.</summary>
    public class StateDiffArchiveMainProcessingModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) =>
            builder.AddDecorator<IWorldStateScopeProvider>((ctx, inner) =>
            {
                StateDiffStore store = ctx.Resolve<StateDiffStore>();
                ILogManager logManager = ctx.Resolve<ILogManager>();

                if (store.RecordingEnabled)
                    return new RecordingScopeProvider(inner, store, logManager);
                if (store.ReplayEnabled)
                    return new ReplayScopeProvider(inner, ctx.Resolve<ReplayScopeTracker>(), store.VerifyStateRoot, logManager);
                return inner;
            });
    }
}
