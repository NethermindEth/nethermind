// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class SenderEndpointCacheTests
{
    [Test]
    public void Resolves_each_sender_written_into_a_reused_address()
    {
        IPEndPoint first = new(IPAddress.Parse("::ffff:192.0.2.1"), 30303);
        IPEndPoint second = new(IPAddress.Parse("2001:db8::1"), 30304);
        SocketAddress received = new(AddressFamily.InterNetworkV6);
        SenderEndpointCache cache = new(capacity: 16);

        IPEndPoint firstResolved = Resolve(cache, received, first);
        IPEndPoint secondResolved = Resolve(cache, received, second);
        IPEndPoint firstAgain = Resolve(cache, received, first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResolved, Is.EqualTo(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 30303)));
            Assert.That(secondResolved, Is.EqualTo(second));
            Assert.That(firstAgain, Is.SameAs(firstResolved));
        }
    }

    [Test]
    public void Clears_when_full()
    {
        IPEndPoint first = new(IPAddress.Parse("192.0.2.1"), 30303);
        IPEndPoint second = new(IPAddress.Parse("192.0.2.2"), 30303);
        SocketAddress received = new(AddressFamily.InterNetwork);
        SenderEndpointCache cache = new(capacity: 1);

        IPEndPoint firstResolved = Resolve(cache, received, first);
        Resolve(cache, received, second);

        Assert.That(Resolve(cache, received, first), Is.Not.SameAs(firstResolved).And.EqualTo(first));
    }

    private static IPEndPoint Resolve(SenderEndpointCache cache, SocketAddress received, IPEndPoint sender)
    {
        SocketAddress serialized = sender.Serialize();
        received.Size = serialized.Size;
        serialized.Buffer.Span[..serialized.Size].CopyTo(received.Buffer.Span);
        return cache.GetOrAdd(received);
    }
}
