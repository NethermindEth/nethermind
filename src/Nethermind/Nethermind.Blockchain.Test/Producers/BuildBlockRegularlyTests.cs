// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

[Parallelizable(ParallelScope.All)]
public class BuildBlockRegularlyTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Regular_trigger_works()
    {
        int triggered = 0;
        using SemaphoreSlim fired = new(0);
        using BuildBlocksRegularly trigger = new(TimeSpan.FromMilliseconds(5));
        trigger.TriggerBlockProduction += (s, e) =>
        {
            Interlocked.Increment(ref triggered);
            fired.Release();
        };

        for (int i = 0; i < 3; i++)
        {
            bool got = await fired.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(got, Is.True, $"Trigger #{i + 1} did not fire within 5 seconds");
        }
    }
}
