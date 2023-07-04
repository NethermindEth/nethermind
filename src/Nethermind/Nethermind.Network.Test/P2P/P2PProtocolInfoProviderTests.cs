// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class P2PProtocolInfoProviderTests
    {
        [Test]
        public void GetHighestVersionOfEthProtocol_ReturnExpectedResult()
        {
            int result = P2PProtocolInfoProvider.GetHighestVersionOfEthProtocol();
            Assert.That(result, Is.EqualTo(66));
        }

        [Test]
        public void DefaultCapabilitiesToString_ReturnExpectedResult()
        {
            string result = P2PProtocolInfoProvider.DefaultCapabilitiesToString();
            Assert.That(result, Is.EqualTo("eth/66"));
        }
    }
}
