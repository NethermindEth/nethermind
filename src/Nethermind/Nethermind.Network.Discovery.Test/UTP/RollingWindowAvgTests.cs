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
        FixedRollingAvg avg = new FixedRollingAvg(5, 99, 100);
        avg.GetAvg(99);
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
