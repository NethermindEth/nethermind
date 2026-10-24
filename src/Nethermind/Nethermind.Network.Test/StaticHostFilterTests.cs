// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;
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

    [Test]
    public async Task Refresh_demotes_only_promoted_nodes_at_dropped_addresses()
    {
        IPAddress[] result = [IPAddress.Parse("5.6.7.8"), IPAddress.Parse("5.6.7.9")];
        StaticHostFilter filter = Create("my-node.example", (_, _) => Task.FromResult(result));
        await filter.RefreshAsync(CancellationToken.None);

        Node dropped = new(TestItem.PublicKeyA, "5.6.7.8", 30303);
        Node kept = new(TestItem.PublicKeyB, "5.6.7.9", 30303);
        Node configured = new(TestItem.PublicKeyC, "5.6.7.8", 30304) { IsStatic = true };
        Assert.That(filter.TryPromote(dropped, matchPort: true), Is.True);
        Assert.That(filter.TryPromote(kept, matchPort: true), Is.True);
        Assert.That(filter.TryPromote(configured, matchPort: true), Is.False);

        result = [IPAddress.Parse("5.6.7.9")];
        await filter.RefreshAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dropped.IsStatic, Is.False);
            Assert.That(kept.IsStatic, Is.True);
            Assert.That(configured.IsStatic, Is.True);
        }
    }

    [Test]
    public async Task Refresh_keeps_the_match_mode_of_each_promotion()
    {
        IPAddress[] result = [IPAddress.Parse("1.2.3.4")];
        StaticHostFilter filter = Create("dynamic.example:30303,1.2.3.4:30304", (_, _) => Task.FromResult(result));
        await filter.RefreshAsync(CancellationToken.None);

        Node discovered = new(TestItem.PublicKeyA, "1.2.3.4", 30303);
        Node inbound = new(TestItem.PublicKeyB, "1.2.3.4", 40000);
        Assert.That(filter.TryPromote(discovered, matchPort: true), Is.True);
        Assert.That(filter.TryPromote(inbound, matchPort: false), Is.True);

        result = [IPAddress.Parse("5.6.7.8")];
        await filter.RefreshAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(discovered.IsStatic, Is.False);
            Assert.That(inbound.IsStatic, Is.True);
        }
    }

    [Test]
    public void Untracked_node_is_not_demoted()
    {
        StaticHostFilter filter = Create("1.2.3.4");
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 30303);
        filter.TryPromote(node, matchPort: true);

        filter.Untrack(node);
        filter.Demote(node);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.IsStatic, Is.True);
            Assert.That(filter.IsPromoted(node), Is.False);
        }
    }

    [Test]
    public void Mark_proven_promotes_a_matching_node()
    {
        StaticHostFilter filter = Create("1.2.3.4:30303");
        Node matching = new(TestItem.PublicKeyA, "1.2.3.4", 30303);
        Node otherPort = new(TestItem.PublicKeyB, "1.2.3.4", 30304);

        filter.MarkProven(matching);
        filter.MarkProven(otherPort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(matching.IsStatic, Is.True);
            Assert.That(filter.IsPromoted(matching), Is.True);
            Assert.That(filter.IsProven(matching), Is.True);
            Assert.That(otherPort.IsStatic, Is.False);
            Assert.That(filter.IsProven(otherPort), Is.True);
            Assert.That(StaticHostFilter.Empty.IsProven(matching), Is.False);
        }
    }

    [Test]
    public void Refresh_survives_a_throwing_changed_handler()
    {
        StaticHostFilter filter = Create("my-node.example", static (_, _) => Task.FromResult(new[] { IPAddress.Parse("5.6.7.8") }));
        filter.Changed += static (_, _) => throw new InvalidOperationException();

        Assert.That(() => filter.RefreshAsync(CancellationToken.None), Throws.Nothing);
        Assert.That(filter.IsMatch(IPAddress.Parse("5.6.7.8")), Is.True);
    }

    [Test]
    public void Dispose_can_be_called_twice()
    {
        StaticHostFilter filter = new(new NetworkConfig { StaticPeerHosts = "my-node.example" }, LimboLogs.Instance);
        filter.Dispose();
        Assert.That(filter.Dispose, Throws.Nothing);
    }
}
