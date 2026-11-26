// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
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
            new MergeConfig { Enabled = mergeEnabled },
            new Lazy<IEngineRequestsTracker>(() => engineRequestsTracker));

        await step.Execute(CancellationToken.None);

        await engineRequestsTracker.Received(expectedStartCalls).StartAsync();
    }
}
