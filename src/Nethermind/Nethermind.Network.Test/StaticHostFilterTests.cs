// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class StaticHostFilterTests
{
    private static StaticHostFilter Create(string? config, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        new(config, resolve ?? (static (_, _) => Task.FromResult(Array.Empty<IPAddress>())), LimboLogs.Instance.GetClassLogger<StaticHostFilter>());

    [TestCase("1.2.3.4", "1.2.3.4", 30303, true)]
    [TestCase("1.2.3.4", "1.2.3.5", 30303, false)]
    [TestCase("1.2.3.4", "::ffff:1.2.3.4", 30303, true)]
    [TestCase("1.2.3.4:30303", "1.2.3.4", 30303, true)]
    [TestCase("1.2.3.4:30303", "1.2.3.4", 30304, false)]
    [TestCase("enode://*@1.2.3.4:30303", "1.2.3.4", 30303, true)]
    [TestCase("10.0.0.0/8", "10.20.30.40", 1, true)]
    [TestCase("10.0.0.0/8", "11.0.0.1", 1, false)]
    [TestCase("2001:db8::1", "2001:db8::1", 30303, true)]
    [TestCase("2001:db8::1", "2001:db8::2", 30303, false)]
    [TestCase("[2001:db8::1]:30303", "2001:db8::1", 30303, true)]
    [TestCase("[2001:db8::1]:30303", "2001:db8::1", 30304, false)]
    [TestCase("enode://*@[2001:db8::1]:30303", "2001:db8::1", 30303, true)]
    [TestCase("2001:db8::/32", "2001:db8:1::5", 30303, true)]
    [TestCase("2001:db8::/32", "2001:db9::1", 30303, false)]
    [TestCase("10.0.0.0/8", "2001:db8::1", 30303, false)]
    [TestCase(" 5.5.5.5 , 1.2.3.4 ", "1.2.3.4", 30303, true)]
    [TestCase("not a host!, 1.2.3.4", "1.2.3.4", 30303, true)]
    [TestCase("not a host!", "1.2.3.4", 30303, false)]
    [TestCase("10.0.0.1/8", "10.9.9.9", 30303, true)]
    [TestCase("", "1.2.3.4", 30303, false)]
    [TestCase(null, "1.2.3.4", 30303, false)]
    public void Matches_ip_cidr_and_port(string? config, string address, int port, bool expected) =>
        Assert.That(Create(config).IsMatch(new IPEndPoint(IPAddress.Parse(address), port)), Is.EqualTo(expected));

    [TestCase("1.2.3.4:30303", "1.2.3.4", true)]
    [TestCase("[2001:db8::1]:30303", "2001:db8::1", true)]
    [TestCase("1.2.3.4:30303", "1.2.3.5", false)]
    public void Address_only_match_ignores_port(string config, string address, bool expected) =>
        Assert.That(Create(config).IsMatch(IPAddress.Parse(address)), Is.EqualTo(expected));

    [TestCase("my-node.example", 30304, true)]
    [TestCase("my-node.example:30303", 30303, true)]
    [TestCase("my-node.example:30303", 30304, false)]
    [TestCase("enode://*@my-node.example:30303", 30303, true)]
    public async Task Matches_resolved_hostname(string config, int port, bool expected)
    {
        IPEndPoint ipv4 = new(IPAddress.Parse("5.6.7.8"), port);
        IPEndPoint ipv6 = new(IPAddress.Parse("2001:db8::8"), port);
        StaticHostFilter filter = Create(config, static (host, _) =>
            Task.FromResult(host == "my-node.example" ? new[] { IPAddress.Parse("5.6.7.8"), IPAddress.Parse("2001:db8::8") } : []));

        Assert.That(filter.IsMatch(ipv4), Is.False, "not resolved yet");

        await filter.RefreshAsync(CancellationToken.None);

        Assert.That(filter.IsMatch(ipv4), Is.EqualTo(expected));
        Assert.That(filter.IsMatch(ipv6), Is.EqualTo(expected));
        Assert.That(filter.IsMatch(new IPEndPoint(IPAddress.Parse("5.6.7.9"), port)), Is.False);
    }

    [Test]
    public async Task Refresh_raises_changed_only_when_addresses_change_and_keeps_them_on_failure()
    {
        IPAddress[] result = [IPAddress.Parse("5.6.7.8")];
        bool fail = false;
        StaticHostFilter filter = Create("my-node.example", (_, _) => fail ? throw new SocketException() : Task.FromResult(result));
        int changed = 0;
        filter.Changed += (_, _) => changed++;

        await filter.RefreshAsync(CancellationToken.None);
        await filter.RefreshAsync(CancellationToken.None);
        Assert.That(changed, Is.EqualTo(1));

        fail = true;
        await filter.RefreshAsync(CancellationToken.None);
        Assert.That(filter.IsMatch(IPAddress.Parse("5.6.7.8")), Is.True, "a failed lookup keeps the last addresses");

        fail = false;
        result = [IPAddress.Parse("5.6.7.9")];
        await filter.RefreshAsync(CancellationToken.None);
        Assert.That(changed, Is.EqualTo(2));
        Assert.That(filter.IsMatch(IPAddress.Parse("5.6.7.8")), Is.False);
        Assert.That(filter.IsMatch(IPAddress.Parse("5.6.7.9")), Is.True);
    }
}
