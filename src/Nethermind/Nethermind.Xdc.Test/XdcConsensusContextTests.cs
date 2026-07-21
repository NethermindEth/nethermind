// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class XdcConsensusContextTests
{
    [TestCase(3UL)] // less than current
    [TestCase(5UL)] // equal to current
    public void SetNewRound_RoundNotHigherThanCurrent_IsIgnored(ulong round)
    {
        XdcConsensusContext ctx = new() { CurrentRound = 5 };
        ctx.SetNewRound(round);
        Assert.That(ctx.CurrentRound, Is.EqualTo(5));
    }

    [Test]
    public void SetNewRound_HigherRound_AdvancesRoundAndFiresEvent()
    {
        XdcConsensusContext ctx = new() { CurrentRound = 1, TimeoutCounter = 3 };
        NewRoundEventArgs? receivedArgs = null;
        ctx.NewRoundSetEvent += (_, args) => receivedArgs = args;

        ctx.SetNewRound(5);

        Assert.That(ctx.CurrentRound, Is.EqualTo(5));
        Assert.That(ctx.TimeoutCounter, Is.EqualTo(0));
        Assert.That(receivedArgs, Is.Not.Null);
        Assert.That(receivedArgs!.NewRound, Is.EqualTo(5));
        Assert.That(receivedArgs.PreviousRound, Is.EqualTo(1));
    }

    [Test]
    public void SetNewRound_SameRoundCalledConcurrently_AdvancesExactlyOnce()
    {
        XdcConsensusContext ctx = new() { CurrentRound = 0 };
        int eventCount = 0;
        ctx.NewRoundSetEvent += (_, _) => Interlocked.Increment(ref eventCount);

        Parallel.For(0, 10, _ => ctx.SetNewRound(1));

        Assert.That(ctx.CurrentRound, Is.EqualTo(1));
        Assert.That(eventCount, Is.EqualTo(1));
    }
}
