// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Init;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class VersionToMetricsTests
{
    [TestCase("1", 0)]
    [TestCase("1.2", 0)]
    [TestCase("1.2.3.4", 0)]
    [TestCase("v1.2.3", 0)]
    [TestCase("11.22.333", 1122333)]
    [TestCase("11.22.33", 1122033)]
    [TestCase("11.22.3", 1122003)]
    [TestCase("11.2.3", 1102003)]
    [TestCase("1.2.3", 102003)]
    [TestCase("0.2.3", 2003)]
    [TestCase("0.0.3", 3)]
    [TestCase("0.0.0", 0)]
    [TestCase("11.22.333-prerelease+build", 1122333)]
    [TestCase("11.22.33-prerelease+build", 1122033)]
    [TestCase("11.22.3-prerelease+build", 1122003)]
    [TestCase("11.2.3-prerelease+build", 1102003)]
    [TestCase("1.2.3-prerelease+build", 102003)]
    [TestCase("0.2.3-prerelease+build", 2003)]
    [TestCase("0.0.3-prerelease+build", 3)]
    [TestCase("0.0.0-prerelease+build", 0)]
    [TestCase("11.22.333-prerelease", 1122333)]
    [TestCase("11.22.33-prerelease", 1122033)]
    [TestCase("11.22.3-prerelease", 1122003)]
    [TestCase("11.2.3-prerelease", 1102003)]
    [TestCase("1.2.3-prerelease", 102003)]
    [TestCase("0.2.3-prerelease", 2003)]
    [TestCase("0.0.3-prerelease", 3)]
    [TestCase("0.0.0-prerelease", 0)]
    public void Converts_all_formats(string versionString, int versionNumber)
    {
        VersionToMetrics.ConvertToNumber(versionString).Should().Be(versionNumber);
    }
}
