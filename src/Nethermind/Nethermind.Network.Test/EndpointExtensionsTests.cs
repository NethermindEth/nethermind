// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Transport.Channels;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class EndpointExtensionsTests
{
    [Test]
    public void TryGetLocalIPEndpoint_UsesChannelEndpointSourceAsFallback()
    {
        IPEndPoint expected = new(IPAddress.Loopback, 30303);
        IChannel channel = Substitute.For<IChannel, IIPEndpointSource>();
        ((IIPEndpointSource)channel).IPEndpoint.Returns(expected);

        Assert.That(channel.TryGetLocalIPEndpoint(), Is.SameAs(expected));
    }
}
