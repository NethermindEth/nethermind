using System.Reflection;
using Nethermind.Db.Rocks;
using NUnit.Framework;
using RocksDbSharp;

namespace Nethermind.Db.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public static class RocksDbTests
{
    [Test]
    public static void Should_have_required_version()
    {
        string version = DbOnTheRocks.GetRocksDbVersion();
        Assert.AreEqual("7.7.3", $"{version}", "Unexpected RocksDB version");
    }
}
