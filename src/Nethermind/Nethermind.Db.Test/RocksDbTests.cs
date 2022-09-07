using System.Reflection;
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
        var assembly = Assembly.GetAssembly(typeof(RocksDb));
        var infoAttr = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.IsNotNull(infoAttr, "RocksDB package metadata not found");

        var versions = infoAttr!.InformationalVersion.Split('.');

        Assert.GreaterOrEqual(versions.Length, 3, "Unexpected RocksDB version format");

        var major = versions[0];
        var minor = versions[1];
        var patch = versions[2];

        // Patch version check is needed
        // until the package includes the binaries for aarch64
        Assert.AreEqual("6.29.3", $"{major}.{minor}.{patch}", "Unexpected RocksDB version");
    }
}
