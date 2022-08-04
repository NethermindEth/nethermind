using System.Reflection;
using NUnit.Framework;
using RocksDbSharp;

namespace Nethermind.Db.Test;

[TestFixture]
public static class RocksDbTests
{
    [Test]
    public static void Should_have_required_version()
    {
        var assembly = Assembly.GetAssembly(typeof(RocksDb));
        var infoAttr = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.IsNotNull(infoAttr, "RocksDB package metadata not found");

        var versions = infoAttr!.InformationalVersion.Split('.');

        Assert.GreaterOrEqual(versions.Length, 3, "Unexpected RocksDB version format");

        var major = versions[0];
        var minor = versions[1];

        // Patch version is ignored
        Assert.AreEqual("7.4", $"{major}.{minor}", "Unexpected RocksDB version");
    }
}
