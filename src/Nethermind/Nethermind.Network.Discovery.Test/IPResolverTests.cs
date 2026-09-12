// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.IP;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.All)]
public class IPResolverTests
{
    [Test]
    public void Nethermind_ip_preserves_positional_api()
    {
        IPAddress localIp = IPAddress.Loopback;
        IPAddress externalIp = IPAddress.Parse("192.0.2.1");
        IIPResolver.NethermindIp ip = new(LocalIp: localIp, ExternalIp: externalIp);

        (IPAddress deconstructedLocalIp, IPAddress deconstructedExternalIp) = ip;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deconstructedLocalIp, Is.SameAs(localIp));
            Assert.That(deconstructedExternalIp, Is.SameAs(externalIp));
        }
    }

    [Test]
    public async Task Ipv6_override_uses_automatically_resolved_ipv4_as_primary()
    {
        IPAddress externalIpV6 = IPAddress.Parse("2001:db8::1");
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = externalIpV6.ToString() },
            family => family == AddressFamily.InterNetwork
                ? [SuccessfulSource("8.8.8.8")]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."));

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(externalIpV6));
        }
    }

    [Test]
    public async Task Ipv6_override_becomes_primary_when_ipv4_is_unavailable()
    {
        IPAddress externalIpV6 = IPAddress.Parse("2001:4860:4860::8888");
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = externalIpV6.ToString() },
            family => family == AddressFamily.InterNetwork
                ? []
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."));

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(externalIpV6));
            Assert.That(ip.ExternalIpV4, Is.Null);
            Assert.That(ip.ExternalIpV6, Is.EqualTo(externalIpV6));
        }
    }

    [Test]
    public async Task Automatically_resolved_ipv6_is_used_by_the_enode_provider_when_ipv4_is_unavailable()
    {
        IPAddress externalIpV6 = IPAddress.Parse("2001:4860:4860::8888");
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family == AddressFamily.InterNetworkV6
                ? [SuccessfulSource(externalIpV6.ToString())]
                : []);
        EnodeProvider enodeProvider = new(
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            new NetworkConfig(),
            ipResolver,
            LimboLogs.Instance);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(externalIpV6));
            Assert.That(ip.ExternalIpV4, Is.Null);
            Assert.That(ip.ExternalIpV6, Is.EqualTo(externalIpV6));
            Assert.That(enodeProvider.Enode.HostIp, Is.EqualTo(externalIpV6));
            Assert.That(enodeProvider.Enode.Info, Does.Contain($"@[{externalIpV6}]:"));
        }
    }

    [Test]
    public async Task Missing_external_families_are_resolved_concurrently()
    {
        TaskCompletionSource ipv4Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ipv6Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(bool Success, IPAddress Ip)> ipv4Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(bool Success, IPAddress Ip)> ipv6Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family switch
            {
                AddressFamily.InterNetwork => [Source(ipv4Started, ipv4Result.Task)],
                AddressFamily.InterNetworkV6 => [Source(ipv6Started, ipv6Result.Task)],
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            });

        Task<IIPResolver.NethermindIp> resolution = ipResolver.Resolve().AsTask();
        await Task.WhenAll(ipv4Started.Task, ipv6Started.Task).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.That(resolution.IsCompleted, Is.False);

        ipv4Result.SetResult((true, IPAddress.Parse("8.8.8.8")));
        ipv6Result.SetResult((true, IPAddress.Parse("2001:4860:4860::8888")));
        IIPResolver.NethermindIp ip = await resolution;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(IPAddress.Parse("2001:4860:4860::8888")));
        }
    }

    [Test]
    public async Task Family_override_suppresses_only_its_lookup()
    {
        List<AddressFamily> requestedFamilies = [];
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV4 = "8.8.8.8" },
            family =>
            {
                requestedFamilies.Add(family);
                return [SuccessfulSource("2001:4860:4860::8888")];
            });

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestedFamilies, Is.EqualTo(new[] { AddressFamily.InterNetworkV6 }));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(IPAddress.Parse("2001:4860:4860::8888")));
        }
    }

    [Test]
    public async Task Wrong_family_source_result_is_rejected()
    {
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = "2001:4860:4860::8888" },
            family => family == AddressFamily.InterNetwork
                ? [SuccessfulSource("2001:4860:4860::8844"), SuccessfulSource("8.8.4.4")]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."));

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.4.4")));
    }

    [Test]
    public async Task Invalid_family_override_does_not_suppress_auto_detection()
    {
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig
            {
                ExternalIpV4 = "8.8.8.8",
                ExternalIpV6 = "192.0.2.1"
            },
            family => family == AddressFamily.InterNetworkV6
                ? [SuccessfulSource("2001:4860:4860::8888")]
                : throw new InvalidOperationException("The configured IPv4 family must not be resolved."));

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        Assert.That(ip.ExternalIpV6, Is.EqualTo(IPAddress.Parse("2001:4860:4860::8888")));
    }

    [Test]
    public async Task Unavailable_family_sources_are_tried_once()
    {
        IIPSource ipv4Source = Substitute.For<IIPSource>();
        IIPSource ipv6Source = Substitute.For<IIPSource>();
        ipv4Source.TryGetIP().Returns(Task.FromResult((false, IPAddress.None)));
        ipv6Source.TryGetIP().Returns(Task.FromResult((false, IPAddress.IPv6None)));
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family == AddressFamily.InterNetwork ? [ipv4Source] : [ipv6Source]);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.None));
            Assert.That(ip.ExternalIpV4, Is.Null);
            Assert.That(ip.ExternalIpV6, Is.Null);
            await ipv4Source.Received(1).TryGetIP();
            await ipv6Source.Received(1).TryGetIP();
        }
    }

    [Test]
    public async Task Resolution_is_cached_then_refreshed_after_five_minutes()
    {
        ManualTimeProvider timeProvider = new();
        int sourceCalls = 0;
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = "2001:4860:4860::8888" },
            family => family == AddressFamily.InterNetwork
                ? [SuccessfulSource(++sourceCalls == 1 ? "8.8.8.8" : "8.8.4.4")]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."),
            timeProvider: timeProvider);
        int changes = 0;
        IIPResolver.NethermindIp? observedFromHandler = null;
        ipResolver.Changed += (_, _) =>
        {
            changes++;
            observedFromHandler = ipResolver.Resolve().GetAwaiter().GetResult();
        };

        IIPResolver.NethermindIp first = await ipResolver.Resolve();
        timeProvider.Advance(TimeSpan.FromMinutes(4));
        IIPResolver.NethermindIp cached = await ipResolver.Resolve();
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        IIPResolver.NethermindIp refreshed = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(cached, Is.EqualTo(first));
            Assert.That(refreshed.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.4.4")));
            Assert.That(sourceCalls, Is.EqualTo(2));
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(observedFromHandler, Is.EqualTo(refreshed));
        }
    }

    [Test]
    public async Task Wall_clock_rollback_does_not_postpone_refresh()
    {
        ManualTimeProvider timeProvider = new();
        int sourceCalls = 0;
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = "2001:4860:4860::8888" },
            family => family == AddressFamily.InterNetwork
                ? [SuccessfulSource(++sourceCalls == 1 ? "8.8.8.8" : "8.8.4.4")]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."),
            timeProvider: timeProvider);

        await ipResolver.Resolve();
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        timeProvider.AdjustUtc(TimeSpan.FromDays(-1));
        IIPResolver.NethermindIp refreshed = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshed.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.4.4")));
            Assert.That(sourceCalls, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Failed_refresh_retains_recent_address_then_a_later_success_replaces_it()
    {
        ManualTimeProvider timeProvider = new();
        Queue<(bool Success, IPAddress Ip)> results = new(
        [
            (true, IPAddress.Parse("8.8.8.8")),
            (false, IPAddress.None),
            (true, IPAddress.Parse("8.8.4.4"))
        ]);
        IIPSource source = new StubIpSource(() => Task.FromResult(results.Dequeue()));
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = "2001:4860:4860::8888" },
            family => family == AddressFamily.InterNetwork
                ? [source]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."),
            timeProvider: timeProvider);
        int changes = 0;
        ipResolver.Changed += (_, _) => changes++;

        IIPResolver.NethermindIp initial = await ipResolver.Resolve();
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        IIPResolver.NethermindIp retained = await ipResolver.Resolve();
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        IIPResolver.NethermindIp replaced = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initial.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(retained.ExternalIpV4, Is.EqualTo(initial.ExternalIpV4));
            Assert.That(replaced.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.4.4")));
            Assert.That(results, Is.Empty);
            Assert.That(changes, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Failed_refresh_withdraws_an_address_after_the_stale_limit()
    {
        ManualTimeProvider timeProvider = new();
        Queue<(bool Success, IPAddress Ip)> results = new(
        [
            (true, IPAddress.Parse("8.8.8.8")),
            (false, IPAddress.None)
        ]);
        IIPSource source = new StubIpSource(() => Task.FromResult(results.Dequeue()));
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = "2001:4860:4860::8888" },
            family => family == AddressFamily.InterNetwork
                ? [source]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."),
            timeProvider: timeProvider);

        await ipResolver.Resolve();
        timeProvider.Advance(TimeSpan.FromMinutes(61));
        IIPResolver.NethermindIp expired = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expired.ExternalIp, Is.EqualTo(IPAddress.Parse("2001:4860:4860::8888")));
            Assert.That(expired.ExternalIpV4, Is.Null);
            Assert.That(results, Is.Empty);
        }
    }

    [Test]
    public async Task Caller_cancellation_does_not_cancel_shared_resolution()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(bool Success, IPAddress Ip)> sourceResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = "2001:4860:4860::8888" },
            family => family == AddressFamily.InterNetwork
                ? [Source(started, sourceResult.Task)]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."));
        using CancellationTokenSource cancellation = new();

        Task<IIPResolver.NethermindIp> cancelledWait = ipResolver.Resolve(cancellation.Token).AsTask();
        await started.Task;
        cancellation.Cancel();

        Assert.That(async () => await cancelledWait, Throws.InstanceOf<OperationCanceledException>());

        sourceResult.SetResult((true, IPAddress.Parse("8.8.8.8")));
        IIPResolver.NethermindIp completed = await ipResolver.Resolve();

        Assert.That(completed.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
    }

    [Test]
    public async Task Source_enumeration_failure_does_not_fault_cached_resolution()
    {
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            _ => throw new InvalidOperationException("Source factory failed."));

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.None));
            Assert.That(ip.ExternalIpV4, Is.Null);
            Assert.That(ip.ExternalIpV6, Is.Null);
        }
    }

    [TestCase("8.8.8.8", 30303, "/ip4/8.8.8.8/tcp/30303")]
    [TestCase("::ffff:192.0.2.1", 30304, "/ip4/192.0.2.1/tcp/30304")]
    [TestCase("2001:db8::1", 30305, "/ip6/2001:db8::1/tcp/30305")]
    public void Tcp_multiaddress_uses_the_resolved_address_family(string address, int port, string expected)
        => Assert.That(NetworkHelper.ToTcpMultiaddress(IPAddress.Parse(address), port), Is.EqualTo(expected));

    [TestCase("99.99.99.99", "99.99.99.99", "99.99.99.99", null)]
    [TestCase("10.50.50.50", "10.50.50.50", "10.50.50.50", null)]
    [TestCase("::ffff:192.0.2.1", "192.0.2.1", "192.0.2.1", null)]
    [TestCase("2001:db8::1", "2001:db8::1", null, "2001:db8::1")]
    public async Task Can_resolve_external_ip_with_override(
        string ipOverride,
        string expectedExternalIp,
        string? expectedExternalIpV4,
        string? expectedExternalIpV6)
    {
        INetworkConfig networkConfig = new NetworkConfig { ExternalIp = ipOverride };
        IPResolver ipResolver = CreateResolver(networkConfig);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse(expectedExternalIp)));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(expectedExternalIpV4 is null ? null : IPAddress.Parse(expectedExternalIpV4)));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(expectedExternalIpV6 is null ? null : IPAddress.Parse(expectedExternalIpV6)));
        }
    }

    [Test]
    public async Task Can_resolve_dual_stack_external_ip_overrides()
    {
        INetworkConfig networkConfig = new NetworkConfig
        {
            ExternalIpV4 = "192.0.2.1",
            ExternalIpV6 = "2001:db8::1"
        };
        IPResolver ipResolver = CreateResolver(networkConfig);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(IPAddress.Parse("2001:db8::1")));
        }
    }

    [Test]
    public async Task Invalid_ipv6_override_is_ignored([Values("192.0.2.1", "::", "::ffff:198.51.100.2")] string externalIpV6)
    {
        INetworkConfig networkConfig = new NetworkConfig
        {
            ExternalIpV4 = "192.0.2.1",
            ExternalIpV6 = externalIpV6
        };
        IPResolver ipResolver = CreateResolver(networkConfig);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        Assert.That(ip.ExternalIpV6, Is.Null);
    }

    [TestCase("192.0.2.1", null, null, "192.0.2.1", null)]
    [TestCase("2001:db8::1", null, null, null, "2001:db8::1")]
    [TestCase("::ffff:198.51.100.2", null, null, "198.51.100.2", null)]
    [TestCase("192.0.2.1", null, "2001:db8::1", "192.0.2.1", "2001:db8::1")]
    [TestCase("2001:db8::1", "192.0.2.1", null, "192.0.2.1", "2001:db8::1")]
    [TestCase("192.0.2.1", null, "192.0.2.2", "192.0.2.1", null)] // wrong-family override ignored
    [TestCase("192.0.2.1", null, "::", "192.0.2.1", null)] // unspecified override ignored
    public void NethermindIp_derives_family_addresses(
        string externalIp,
        string? externalIpV4,
        string? externalIpV6,
        string? expectedIpV4,
        string? expectedIpV6)
    {
        IIPResolver.NethermindIp ip = new(
            IPAddress.Loopback,
            IPAddress.Parse(externalIp),
            externalIpV4 is null ? null : IPAddress.Parse(externalIpV4),
            externalIpV6 is null ? null : IPAddress.Parse(externalIpV6));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIpV4, Is.EqualTo(expectedIpV4 is null ? null : IPAddress.Parse(expectedIpV4)));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(expectedIpV6 is null ? null : IPAddress.Parse(expectedIpV6)));
        }
    }

    [Test]
    public void NethermindIp_recomputes_derived_family_addresses_after_with_expression()
    {
        IIPResolver.NethermindIp original = new(IPAddress.Loopback, IPAddress.Parse("2001:db8::1"));

        IIPResolver.NethermindIp changed = original with { ExternalIp = IPAddress.Parse("192.0.2.1") };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.ExternalIpV4, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
            Assert.That(changed.ExternalIpV6, Is.Null);
        }
    }

    [TestCase("2001:db8::1", "192.0.2.1", null, "2001:db8::2", "192.0.2.1", "2001:db8::2")]
    [TestCase("192.0.2.1", null, "2001:db8::1", "198.51.100.1", "198.51.100.1", "2001:db8::1")]
    public void NethermindIp_preserves_explicit_family_override_after_with_expression(
        string externalIp,
        string? externalIpV4,
        string? externalIpV6,
        string changedExternalIp,
        string expectedIpV4,
        string expectedIpV6)
    {
        IIPResolver.NethermindIp original = new(
            IPAddress.Loopback,
            IPAddress.Parse(externalIp),
            externalIpV4 is null ? null : IPAddress.Parse(externalIpV4),
            externalIpV6 is null ? null : IPAddress.Parse(externalIpV6));

        IIPResolver.NethermindIp changed = original with { ExternalIp = IPAddress.Parse(changedExternalIp) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.ExternalIpV4, Is.EqualTo(IPAddress.Parse(expectedIpV4)));
            Assert.That(changed.ExternalIpV6, Is.EqualTo(IPAddress.Parse(expectedIpV6)));
        }
    }

    [Test]
    public void NethermindIp_equality_compares_resolved_addresses()
    {
        IPAddress externalIpV6 = IPAddress.Parse("2001:db8::1");
        IIPResolver.NethermindIp derived = new(IPAddress.IPv6Any, externalIpV6);
        IIPResolver.NethermindIp overridden = new(
            IPAddress.IPv6Any,
            externalIpV6,
            externalIpV4: null,
            externalIpV6);

        Assert.That(overridden, Is.EqualTo(derived));
        Assert.That(overridden.GetHashCode(), Is.EqualTo(derived.GetHashCode()));
    }

    [TestCase("192.0.2.1", "192.0.2.2", null, nameof(NetworkConfig.ExternalIpV4))]
    [TestCase("2001:db8::1", null, "2001:db8::2", nameof(NetworkConfig.ExternalIpV6))]
    public async Task Warns_when_primary_and_family_override_disagree(
        string externalIp,
        string? externalIpV4,
        string? externalIpV6,
        string familyConfigName)
    {
        InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
        underlyingLogger.IsWarn.Returns(true);
        ILogger logger = new(underlyingLogger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<IPResolver>().Returns(logger);
        IPResolver ipResolver = CreateResolver(
            new NetworkConfig
            {
                ExternalIp = externalIp,
                ExternalIpV4 = externalIpV4,
                ExternalIpV6 = externalIpV6
            },
            _ => [],
            logManager);

        await ipResolver.Resolve();

        underlyingLogger.Received(1).Warn(Arg.Is<string>(message =>
            message.Contains($"disagrees with {familyConfigName}")));
    }

    [Test]
    public async Task Can_resolve_local_ip_with_override()
    {
        const string ipOverride = "99.99.99.99";
        INetworkConfig networkConfig = new NetworkConfig { LocalIp = ipOverride };
        IPResolver ipResolver = CreateResolver(networkConfig);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve();
        Assert.That(ip.LocalIp, Is.EqualTo(IPAddress.Parse(ipOverride)));
    }

    private static IPResolver CreateResolver(
        INetworkConfig networkConfig,
        Func<AddressFamily, IEnumerable<IIPSource>>? externalIpSources = null,
        ILogManager? logManager = null,
        TimeProvider? timeProvider = null)
        => new(networkConfig, logManager ?? LimboLogs.Instance, externalIpSources ?? (_ => []), timeProvider);

    private static IIPSource SuccessfulSource(string address)
        => new StubIpSource(() => Task.FromResult((true, IPAddress.Parse(address))));

    private static IIPSource Source(TaskCompletionSource started, Task<(bool Success, IPAddress Ip)> result)
        => new StubIpSource(() =>
        {
            started.SetResult();
            return result;
        });

    private sealed class StubIpSource(Func<Task<(bool Success, IPAddress Ip)>> resolve) : IIPSource
    {
        public Task<(bool Success, IPAddress Ip)> TryGetIP() => resolve();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan timeSpan)
        {
            _utcNow += timeSpan;
            _timestamp += timeSpan.Ticks;
        }

        public void AdjustUtc(TimeSpan timeSpan) => _utcNow += timeSpan;
    }
}
