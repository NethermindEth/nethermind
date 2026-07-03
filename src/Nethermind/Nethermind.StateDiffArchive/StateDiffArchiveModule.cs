// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
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
            // Scoped, not singleton: each processing env (main, prewarmer, ...) gets its own tracker so a
            // prewarmer scope cannot clobber the scope the main env is replaying into.
            builder.AddScoped<ReplayScopeTracker>();
            builder.AddDecorator<IBlockProcessor, ReplayBlockProcessor>();
        }
    }

    /// <summary>
    /// Runs inside the main block-processing scope, the only scope where the global <see cref="IWorldStateScopeProvider"/>
    /// and <see cref="IBlockCachePreWarmer"/> are registered and thus decoratable.
    /// </summary>
    public class StateDiffArchiveMainProcessingModule(IStateDiffArchiveConfig config, IBlocksConfig blocksConfig) : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.AddDecorator<IWorldStateScopeProvider>((ctx, inner) =>
            {
                StateDiffStore store = ctx.Resolve<StateDiffStore>();
                ILogManager logManager = ctx.Resolve<ILogManager>();

                if (store.RecordingEnabled)
                    return new RecordingScopeProvider(inner, store, logManager);
                if (store.ReplayEnabled)
                    return new ReplayScopeProvider(inner, ctx.Resolve<ReplayScopeTracker>(), logManager);
                return inner;
            });

            // During replay, skip the block cache prewarmer for blocks we will replay (no EVM to warm), keeping
            // it for blocks past the archive. Only when prewarming is on, else IBlockCachePreWarmer is unregistered.
            bool replay = config.ReplayEnabled && !config.RecordingEnabled;
            if (replay && blocksConfig.PreWarmStateOnBlockProcessing)
                builder.AddDecorator<IBlockCachePreWarmer>((ctx, inner) => new ReplayPrewarmGate(inner, ctx.Resolve<StateDiffStore>()));
        }
    }
}
