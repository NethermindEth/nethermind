using Lantern.Discv5.WireProtocol.Table;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class TableUtilityTests
{
    [Test]
    public void Test_TableUtility_ShouldCalculateNodeDistanceCorrectly()
    {
        var firstNodeId = Convert.FromHexString("EE35DAD535348E43545127C39DF1CF460B0FD6D012D44D6196899D4A8C967D92");
        var secondNodeId = Convert.FromHexString("4A2E2A02F3DE741C7DB65A4E431BBD7FFF02C9C2AA3AD7E4E2999D906483EC4A");
        var firstDistance = TableUtility.Log2Distance(firstNodeId, secondNodeId);

        Assert.AreEqual(256, firstDistance);
    }

    [Test]
    public void Test_TableUtility_ShouldEqualZeroDistanceForSameNodeIds()
    {
        var firstNodeId = Convert.FromHexString("4A2E2A02F3DE741C7DB65A4E431BBD7FFF02C9C2AA3AD7E4E2999D906483EC4A");
        var secondNodeId = Convert.FromHexString("4A2E2A02F3DE741C7DB65A4E431BBD7FFF02C9C2AA3AD7E4E2999D906483EC4A");
        var firstDistance = TableUtility.Log2Distance(firstNodeId, secondNodeId);

        Assert.AreEqual(0, firstDistance);
    }
}
