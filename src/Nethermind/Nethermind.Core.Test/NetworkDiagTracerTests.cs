// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class NetworkDiagTracerTests
    {
        [Test]
        public void Test() => Assert.That(NetworkDiagTracer.NetworkDiagTracerPath, Does.Not.StartWith("C:"));
    }
}
