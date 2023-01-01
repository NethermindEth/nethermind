// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        Assert.AreEqual("6.29.3", $"{version}", "Unexpected RocksDB version");
    }
}
