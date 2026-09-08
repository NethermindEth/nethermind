// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test.Replay;

public class StubJsonRpcServerTests
{
    [Test]
    public async Task Surfaces_a_connection_failure_that_is_not_part_of_shutdown()
    {
        // The stub swallows only teardown races. A connection dying mid-request while the server is
        // live is a broken test environment; hiding it would fail some other assertion with nothing
        // pointing at the cause.
        StubJsonRpcServer server = new();

        using (TcpClient client = new())
        {
            await client.ConnectAsync(IPAddress.Loopback, server.Address.Port);
            byte[] partial = Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nContent-Length: 10\r\n\r\nabc");
            await client.GetStream().WriteAsync(partial);
        }

        await WaitForFailureAsync(server);

        Assert.That(async () => await server.DisposeAsync(), Throws.InstanceOf<IOException>());
    }

    [Test]
    public async Task Disposes_cleanly_when_a_client_closes_between_requests()
    {
        // Closing between requests is how every level of a sweep ends; it must never read as a failure.
        StubJsonRpcServer server = new();

        using (TcpClient client = new())
        {
            await client.ConnectAsync(IPAddress.Loopback, server.Address.Port);
        }

        Assert.That(async () => await server.DisposeAsync(), Throws.Nothing);
    }

    /// <summary>Waits until the failure surfaces, so shutdown cannot race the read that observes it.</summary>
    private static async Task WaitForFailureAsync(StubJsonRpcServer server)
    {
        for (int i = 0; i < 500 && server.Failures.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.That(server.Failures, Is.Not.Empty, "the aborted request surfaced while the server was live");
    }
}
