using System;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Tests;

public class LEDBATTests
{
    [Test]
    public void TestRolling()
    {
        LEDBAT ledbat = new LEDBAT(LimboLogs.Instance);

        ledbat.OnAck(1000, 500, 500, 0);

        Assert.That(ledbat.WindowSize, Is.EqualTo(32 * 500));
    }

    [Test]
    public void TestSlowStartAndSsThres()
    {
        LEDBAT ledbat = new LEDBAT(true, 1000 * 500, LimboLogs.Instance);

        ledbat.OnAck(500, 0, 150_000, 200_00);

        Assert.IsFalse(ledbat.getIsSlowStart());
        Assert.That(ledbat.getSsThres(), Is.EqualTo((1000 * 500) / 2));
    }

    [Test]
    public void TestWindowIncrementeDueToSlowStartActivated()
    {
        LEDBAT ledbat = new LEDBAT(LimboLogs.Instance);
        uint initialWindowSize = ledbat.WindowSize;
        ledbat.OnAck(5000, 250000, 0, 0);
        Assert.Greater(ledbat.WindowSize, initialWindowSize);
    }

    [Test]
    public void TestLimitWindowSizeWhenCwndSaturated()
    {
        LEDBAT ledbat = new LEDBAT(false, 32 * 500, LimboLogs.Instance);
        ledbat.OnAck(500, 500_000, 0, 0);

        uint maxAllowedCwnd = (uint)(500_000 + ledbat.getALLOWED_INCREASE() * ledbat.getMSS());
        Assert.LessOrEqual(ledbat.WindowSize, maxAllowedCwnd);
        Assert.GreaterOrEqual(ledbat.WindowSize, ledbat.getMIN_CWND() * ledbat.getMSS());
    }

    [Test]
    public void TestWindowSizeReducedToHalfOrMinOnDataLossT()
    {
        LEDBAT ledbat = new LEDBAT(LimboLogs.Instance);
        ledbat.OnDataLoss(200_000);

        uint expectedWindowSize = Math.Max(ledbat.WindowSize / 2, ledbat.getMIN_CWND() * ledbat.getMSS());
        Assert.That(ledbat.WindowSize, Is.EqualTo(expectedWindowSize));
        Assert.IsFalse(ledbat.getIsSlowStart());
    }
}
