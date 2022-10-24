using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Timers;
using NUnit.Framework;

namespace Nethermind.Core.Test.Timers;

public class FunctionalTimerTests
{
    [Test]
    public async Task Timer_should_trigger_task_repeatedly()
    {
        using CancellationTokenSource cts = new();
        int triggerCount = 0;

        cts.CancelAfter(TimeSpan.FromMilliseconds(550));

        await FunctionalTimer.RunEvery(TimeSpan.FromMilliseconds(100), cts.Token, token => triggerCount++);

        triggerCount.Should().Be(6);
    }
}
