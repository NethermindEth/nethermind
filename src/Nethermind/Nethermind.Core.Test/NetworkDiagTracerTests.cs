// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class NetworkDiagTracerTests
    {
        [Test]
        public void Test()
        {
            NetworkDiagTracer.NetworkDiagTracerPath.Should().NotStartWith("C:");
        }
    }
}
