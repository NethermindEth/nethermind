// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public class BuildBlockRegularlyTests
{
    [Test, MaxTime(Timeout.MaxTestTime), Retry(3)]
    public async Task Regular_trigger_works()
    {
        int triggered = 0;
        BuildBlocksRegularly trigger = new(TimeSpan.FromMilliseconds(5));
        trigger.TriggerBlockProduction += (s, e) => triggered++;
        await Task.Delay(50);

        triggered.Should().BeInRange(1, 20);
    }
}
