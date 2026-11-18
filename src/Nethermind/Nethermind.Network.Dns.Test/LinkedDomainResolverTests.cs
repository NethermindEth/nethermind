// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DnsClient;
using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

public class LinkedDomainResolverTests
{
    [Test]
    public async Task Raw_link_domain_is_sent_literally_and_nxdomain_returns_no_answers()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        LookupClient client = new(new LookupClientOptions((IPEndPoint)server.Client.LocalEndPoint!)
        {
            UseTcpFallback = false,
            UseCache = false,
            Retries = 0,
            Timeout = TimeSpan.FromSeconds(2)
        });
        Task<IDnsQueryResponse> lookup = client.QueryAsync(new DnsQuestion("HASH.KEY@domain.test", QueryType.TXT), timeout.Token);
        UdpReceiveResult request = await server.ReceiveAsync(timeout.Token);
        int end = 12;
        List<string> labels = [];
        while (request.Buffer[end] != 0)
        {
            int length = request.Buffer[end++];
            labels.Add(Encoding.ASCII.GetString(request.Buffer, end, length));
            end += length;
        }
        end += 5;
        byte[] response = request.Buffer[..end];
        response[2] = 0x81;
        response[3] = 0x83;
        response.AsSpan(6, 6).Clear();
        await server.SendAsync(response, request.RemoteEndPoint, timeout.Token);
        IDnsQueryResponse result = await lookup;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(string.Join('.', labels), Is.EqualTo("HASH.KEY@domain.test").IgnoreCase);
            Assert.That(result.HasError, Is.True);
            Assert.That(result.Answers, Is.Empty);
        }
    }
}
