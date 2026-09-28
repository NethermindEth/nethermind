// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test;

public class StartHealthChecksTests
{
    [TestCase(true, 1)]
    [TestCase(false, 0)]
    public async Task Execute_starts_cl_liveness_tracker_only_when_merge_enabled(bool mergeEnabled, int expectedStartCalls)
    {
        IEngineRequestsTracker engineRequestsTracker = Substitute.For<IEngineRequestsTracker>();
        StartHealthChecks step = new(
            new HealthChecksConfig { LowStorageSpaceShutdownThreshold = 0, LowStorageSpaceWarningThreshold = 0 },
            new MergeConfig { Enabled = mergeEnabled },
            CreateDiskChecker(),
            new Lazy<IEngineRequestsTracker>(() => engineRequestsTracker),
            LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        await engineRequestsTracker.Received(expectedStartCalls).StartAsync();
    }

    private static FreeDiskSpaceChecker CreateDiskChecker() =>
        new(
            new HealthChecksConfig(),
            new[] { Substitute.For<IDriveInfo>() },
            TimerFactory.Default,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);
}
