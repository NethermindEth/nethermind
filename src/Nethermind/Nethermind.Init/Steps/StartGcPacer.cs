// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.Memory;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

/// <summary>Starts the <see cref="GcPacer"/> when any pacing cadence is configured.</summary>
public sealed class StartGcPacer(GcPacer gcPacer, IInitConfig initConfig, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        if (initConfig.GcPaceGen1IntervalMs <= 0 && (initConfig.GcPaceGen2IntervalMs > 0 || initConfig.GcPaceWarmupSeconds > 0))
        {
            ILogger logger = logManager.GetClassLogger<StartGcPacer>();
            if (logger.IsWarn) logger.Warn($"Init.{nameof(IInitConfig.GcPaceGen2IntervalMs)} and Init.{nameof(IInitConfig.GcPaceWarmupSeconds)} have no effect unless Init.{nameof(IInitConfig.GcPaceGen1IntervalMs)} is set.");
        }

        gcPacer.TryStart();
        return Task.CompletedTask;
    }
}
