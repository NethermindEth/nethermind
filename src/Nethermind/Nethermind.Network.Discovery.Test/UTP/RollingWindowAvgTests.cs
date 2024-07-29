using System;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Tests;

public class RollingWindowAvgTests
{
    [Test]
    public void TestRolling()
    {
        FixedRollingAvg avg = new FixedRollingAvg(5, 0, 100);

        int[] observeSequence = [5, 5, 20, 10, 10, 10, 10, 20];
        uint[] expectedAvg = [5, 5, 10, 10, 10, 11, 12, 12];

        for (var i = 0; i < observeSequence.Length; i++)
        {
            avg.Observe(observeSequence[i], 1);
            Assert.That(expectedAvg[i], Is.EqualTo(avg.GetAvg(1)));
        }
    }

    [Test]
    public void TestDefault()
    {
         var avg = new FixedRollingAvg(5, 99, 100);
         Assert.That(avg.GetAvg(99), Is.EqualTo(99));
    }

    [Test]
    public void GetAvgFixed16PrecisionDefaultValueWithRandomInput()
    {

        var rollingAvg  = new FixedRollingAvg(5, 100, 1000);
        int avg = rollingAvg.GetAvg((uint)Random.Shared.Next());

        Assert.That(avg, Is.EqualTo(100));
    }

    [Test]
    public void TestUpdateMin()
    {
        FixedRollingAvg avg = new FixedRollingAvg(5, 99, 100);
        avg.Observe(100, 1);
        Assert.That(avg.GetAvg(1), Is.EqualTo(100));
        avg.AdjustMin(50, 1);
        Assert.That(avg.GetAvg(1), Is.EqualTo(50));
    }

    [Test]
    public void testDelayExceedsExpiry()
    {
        var rollingAvg  = new FixedRollingAvg(5, 100, 1000);
        uint now = 0;
        rollingAvg.Observe(500, now);
        now += 2000;
        int avg = rollingAvg.GetAvg(now);

        Assert.That(avg, Is.EqualTo(100));

    }
    [Test]
    public void Observe_MoreThanCapacity_GetAvg_ReturnsCorrectAverage()
    {
        var rollingAvg  = new FixedRollingAvg(5, 100, 1000);
        uint now = 0;

        // Observe more than capacity
        rollingAvg.Observe(1000, now);
        rollingAvg.Observe(1500, now);
        rollingAvg.Observe(2000, now);
        rollingAvg.Observe(2500, now);
        rollingAvg.Observe(3000, now);
        rollingAvg.Observe(3500, now);

        // Act
        int avg = rollingAvg.GetAvg(now);

        // Assert
        Assert.That(avg, Is.EqualTo(2500));
    }




    [Test]
    public void TestExpiry()
    {
        FixedRollingAvg avg = new FixedRollingAvg(5, 99, 1);

        avg.Observe(5, 1);
        avg.Observe(5, 1);
        avg.Observe(5, 1);
        avg.Observe(5, 1);
        avg.Observe(5, 1);
        Assert.That(avg.GetAvg(1), Is.EqualTo(5));
        Assert.That(avg.GetAvg(2), Is.EqualTo(99));
        avg.Observe(10, 2);
        avg.Observe(10, 2);
        avg.Observe(10, 2);
        avg.Observe(10, 2);
        avg.Observe(10, 2);
        Assert.That(avg.GetAvg(2), Is.EqualTo(10));
    }

}
