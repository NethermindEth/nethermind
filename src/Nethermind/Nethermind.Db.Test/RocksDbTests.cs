using System.Reflection;
using Nethermind.Db.Rocks;
using NUnit.Framework;
using RocksDbSharp;

namespace Nethermind.Db.Test;

[TestFixture]
public static class RocksDbTests
{
    [Test]
    public static void Should_have_required_version()
    {
        string version = DbOnTheRocks.GetRocksDbVersion();
        Assert.AreEqual("6.29.3", $"{version}", "Unexpected RocksDB version");
    }
}
