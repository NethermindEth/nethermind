
using System.Net;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Utility;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class LruCacheTests
{
    private static LruCache<SessionCacheKey, ISessionMain> _lruCache = null!;

    [SetUp]
    public void Setup()
    {
        _lruCache = new LruCache<SessionCacheKey, ISessionMain>(10);
    }

    [Test]
    public void Test_LruCache_AddSessionCorrectly()
    {
        _lruCache.Add(new SessionCacheKey(RandomUtility.GenerateRandomData(32), new IPEndPoint(IPAddress.Loopback, 1234)), null!);
        Assert.AreEqual(1, _lruCache.Count);
    }

    [Test]
    public void Test_LruCache_GetSessionCorrectly()
    {
        var nodeId = RandomUtility.GenerateRandomData(32);
        var sessionKey = new SessionCacheKey(nodeId, new IPEndPoint(IPAddress.Loopback, 1234));

        _lruCache.Add(sessionKey, null!);

        var value = _lruCache.Get(sessionKey);
        Assert.IsNull(value);
    }

    [Test]
    public void Test_LruCache_GetSessionCorrectlyAfterAddingMoreThanCacheSize()
    {
        var nodeId = RandomUtility.GenerateRandomData(32);
        var sessionKey = new SessionCacheKey(nodeId, new IPEndPoint(IPAddress.Loopback, 1234));

        for (var i = 0; i < 11; i++)
        {
            _lruCache.Add(new SessionCacheKey(RandomUtility.GenerateRandomData(32), new IPEndPoint(IPAddress.Loopback, 1234)), null!);
        }

        _lruCache.Add(sessionKey, null!);

        var value = _lruCache.Get(sessionKey);
        Assert.AreEqual(10, _lruCache.Count);
        Assert.IsNull(value);
    }

    [Test]
    public void Test_LruCache_ShouldRefreshNode_WhenTheSameSessionIsAddedTwice()
    {
        var nodeId = RandomUtility.GenerateRandomData(32);
        var sessionKey = new SessionCacheKey(nodeId, new IPEndPoint(IPAddress.Loopback, 1234));
        var sessionValue = new Mock<ISessionMain>().Object;

        _lruCache.Add(sessionKey, null!);
        _lruCache.Add(sessionKey, sessionValue);

        var value = _lruCache.Get(sessionKey);

        Assert.AreEqual(1, _lruCache.Count);
        Assert.AreEqual(sessionValue, value);
    }
}
