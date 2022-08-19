//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
