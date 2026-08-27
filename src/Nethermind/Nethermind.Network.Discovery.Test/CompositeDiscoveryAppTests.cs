// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class CompositeDiscoveryAppTests
{
    [TestCase("0.0.0.0", AddressFamily.InterNetwork, false)]
    [TestCase("127.0.0.1", AddressFamily.InterNetwork, false)]
    [TestCase("::1", AddressFamily.InterNetworkV6, false)]
    [TestCase("::", AddressFamily.InterNetworkV6, true)]
    [TestCase("::ffff:0.0.0.0", AddressFamily.InterNetworkV6, true)]
    public void CreateDatagramSocket_MatchesListenerAddress(string localIp, AddressFamily expectedFamily, bool expectedDualMode)
    {
        using Socket socket = CompositeDiscoveryApp.CreateDatagramSocket(IPAddress.Parse(localIp));

        Assert.That(socket.AddressFamily, Is.EqualTo(expectedFamily));
        if (expectedFamily == AddressFamily.InterNetworkV6)
        {
            Assert.That(socket.DualMode, Is.EqualTo(expectedDualMode));
        }
    }
}
