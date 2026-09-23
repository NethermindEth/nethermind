// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
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
        await using IPResolver ipResolver = CreateResolver(
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
        await using IPResolver ipResolver = CreateResolver(
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
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family == AddressFamily.InterNetworkV6
                ? [SuccessfulSource(externalIpV6.ToString())]
                : []);
        await using IContainer container = CreateContainer(ipResolver);
        EnodeProvider enodeProvider = container.Resolve<EnodeProvider>();

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
        await using IPResolver ipResolver = CreateResolver(
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
        await using IPResolver ipResolver = CreateResolver(
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
    public async Task Automatic_resolution_can_be_disabled_without_ignoring_overrides()
    {
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig
            {
                ExternalIpV4 = "8.8.8.8",
                EnableExternalIpResolution = false
            },
            _ => throw new InvalidOperationException("Automatic sources must not be queried."));

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV6, Is.Null);
        }
    }

    [Test]
    public async Task Unsupported_family_is_not_automatically_resolved()
    {
        List<AddressFamily> requestedFamilies = [];
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family =>
            {
                requestedFamilies.Add(family);
                return family == AddressFamily.InterNetwork ? [SuccessfulSource("8.8.8.8")] : [];
            },
            hasLocalAddressFamily: family => family == AddressFamily.InterNetwork);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestedFamilies, Is.EqualTo(new[] { AddressFamily.InterNetwork }));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV6, Is.Null);
        }
    }

    [TestCase("169.254.10.20", AddressFamily.InterNetwork, false)]
    [TestCase("192.168.10.20", AddressFamily.InterNetwork, true)]
    [TestCase("fe80::1", AddressFamily.InterNetworkV6, false)]
    [TestCase("fd00::1", AddressFamily.InterNetworkV6, true)]
    [TestCase("2001:db8::1", AddressFamily.InterNetworkV6, true)]
    public void Local_address_capability_excludes_link_local_addresses(
        string address,
        AddressFamily family,
        bool expected)
        => Assert.That(IPResolver.IsUsableLocalAddress(IPAddress.Parse(address), family), Is.EqualTo(expected));

    [TestCase("2001:4860:4860::8844", TestName = "Wrong-family source result is rejected")]
    [TestCase("192.0.2.1", TestName = "Special-use source result is rejected")]
    [TestCase("224.0.0.1", TestName = "Multicast source result is rejected")]
    public async Task Unusable_source_result_is_rejected(string unusable)
    {
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = "2001:4860:4860::8888" },
            family => family == AddressFamily.InterNetwork
                ? [SuccessfulSource(unusable), SuccessfulSource("8.8.4.4")]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."));

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.4.4")));
    }

    [Test]
    public async Task Invalid_family_override_does_not_suppress_auto_detection()
    {
        await using IPResolver ipResolver = CreateResolver(
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
    public async Task Unavailable_family_source_is_tried_once_per_attempt()
    {
        IIPSource ipv6Source = Substitute.For<IIPSource>();
        ipv6Source.TryGetIP(Arg.Any<CancellationToken>()).Returns(Task.FromResult((false, IPAddress.IPv6None)));
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family == AddressFamily.InterNetwork ? [SuccessfulSource("8.8.8.8")] : [ipv6Source]);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(ip.ExternalIpV6, Is.Null);
            await ipv6Source.Received(1).TryGetIP(Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task V5_record_refreshes_without_new_heads_or_resolver_reads()
    {
        ManualTimeProvider timeProvider = new();
        Queue<(bool Success, IPAddress Ip)> results = new(
        [
            (true, IPAddress.Parse("8.8.8.8")),
            (true, IPAddress.Parse("8.8.4.4")),
            (true, IPAddress.Parse("1.1.1.1"))
        ]);
        IIPSource source = new StubIpSource(() => Task.FromResult(results.Dequeue()));
        await using IPResolver ipResolver = CreateResolver(new NetworkConfig(),
            family => family == AddressFamily.InterNetwork ? [source] : [], timeProvider: timeProvider);
        await using IContainer container = CreateContainer(ipResolver);
        container.Resolve<IBlockTree>().SuggestBlock(Build.A.Block.Genesis.TestObject);
        NetworkListenerState listeners = container.Resolve<NetworkListenerState>();
        listeners.SetRlpxAddress(IPAddress.Any);
        listeners.SetDiscoveryAddress(IPAddress.Any);
        INodeRecordProvider provider = container.Resolve<INodeRecordProvider>();
        NodeRecord initial = await provider.GetCurrentAsync();

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        NodeRecord recovered = await provider.GetCurrentAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        NodeRecord rotated = await provider.GetCurrentAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initial.GetObj<IPAddress>(EnrContentKey.Ip), Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(recovered.GetObj<IPAddress>(EnrContentKey.Ip), Is.EqualTo(IPAddress.Parse("8.8.4.4")));
            Assert.That(rotated.GetObj<IPAddress>(EnrContentKey.Ip), Is.EqualTo(IPAddress.Parse("1.1.1.1")));
            Assert.That(recovered.EnrSequence, Is.GreaterThan(initial.EnrSequence));
            Assert.That(rotated.EnrSequence, Is.GreaterThan(recovered.EnrSequence));
            Assert.That(results, Is.Empty);
        }
    }

    [Test]
    public async Task Dispose_cancels_owned_lookup_and_stops_refresh(
        [Values] bool duringRefresh,
        [Values] bool completeOnCancellation)
    {
        ManualTimeProvider timeProvider = new();
        BlockingIpSource source = new(completeOnCancellation);
        int sourceCalls = 0;
        await using IPResolver ipResolver = CreateResolver(new NetworkConfig(),
            family => family != AddressFamily.InterNetwork ? [] :
                [++sourceCalls == 1 && duringRefresh ? SuccessfulSource("8.8.8.8") : source],
            timeProvider: timeProvider);
        int changes = 0;
        ipResolver.Changed += (_, _) => changes++;
        Task<IIPResolver.NethermindIp> initial = ipResolver.Resolve().AsTask();
        if (duringRefresh)
        {
            await initial;
            timeProvider.Advance(TimeSpan.FromMinutes(5));
        }
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await ipResolver.DisposeAsync();
        if (!duringRefresh)
        {
            Assert.That(async () => await initial, Throws.InstanceOf<OperationCanceledException>());
        }
        timeProvider.Advance(TimeSpan.FromHours(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Cancelled, Is.True);
            Assert.That(sourceCalls, Is.EqualTo(duringRefresh ? 2 : 1));
            Assert.That(changes, Is.Zero);
            Assert.That(() => ipResolver.Resolve(), Throws.TypeOf<ObjectDisposedException>());
        }
    }

    [Test]
    public async Task Non_refreshing_resolver_accepts_change_subscriptions()
    {
        IIPResolver resolver = new FixedIpResolver(new NetworkConfig { ExternalIp = "8.8.8.8" });
        EventHandler handler = (_, _) => Assert.Fail("A fixed resolver must not raise change notifications.");
        resolver.Changed += handler;
        IIPResolver.NethermindIp ip = await resolver.Resolve();
        resolver.Changed -= handler;

        Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
    }

    [Test]
    public async Task Startup_resolution_retries_before_answering_one_shot_consumers()
    {
        ManualTimeProvider timeProvider = new();
        Queue<(bool Success, IPAddress Ip)> results = new(
        [
            (false, IPAddress.None),
            (true, IPAddress.Parse("8.8.8.8"))
        ]);
        IIPSource source = new StubIpSource(() => Task.FromResult(results.Dequeue()));
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family == AddressFamily.InterNetwork ? [source] : [],
            timeProvider: timeProvider,
            hasLocalAddressFamily: family => family == AddressFamily.InterNetwork);

        Task<IIPResolver.NethermindIp> startup = ipResolver.Resolve().AsTask();
        Assert.That(startup.IsCompleted, Is.False, "a transient first failure must not reach consumers that read the address once");
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        IIPResolver.NethermindIp resolved = await startup.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved.ExternalIp, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
            Assert.That(results, Is.Empty);
        }
    }

    [Test]
    public async Task Repeated_unresolved_startup_attempts_return_to_normal_refresh_interval()
    {
        ManualTimeProvider timeProvider = new();
        int sourceCalls = 0;
        IIPSource source = new StubIpSource(() =>
        {
            sourceCalls++;
            return Task.FromResult((false, IPAddress.None));
        });
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family == AddressFamily.InterNetwork ? [source] : [],
            timeProvider: timeProvider,
            hasLocalAddressFamily: family => family == AddressFamily.InterNetwork);

        Task<IIPResolver.NethermindIp> startup = ipResolver.Resolve().AsTask();
        for (int attempt = 1; attempt < 5; attempt++)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(2));
        }

        IIPResolver.NethermindIp ip = await startup.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.None));
            Assert.That(sourceCalls, Is.EqualTo(5));
        }

        timeProvider.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.That(sourceCalls, Is.EqualTo(5));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await ipResolver.Resolve();
        Assert.That(sourceCalls, Is.EqualTo(6));
    }

    [Test]
    public async Task Resolution_is_cached_then_refreshed_after_five_minutes()
    {
        ManualTimeProvider timeProvider = new();
        int sourceCalls = 0;
        await using IPResolver ipResolver = CreateResolver(
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
    public async Task Expired_resolution_keeps_serving_the_cached_result_until_the_refresh_completes()
    {
        ManualTimeProvider timeProvider = new();
        TaskCompletionSource refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(bool Success, IPAddress Ip)> refreshResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sourceCalls = 0;
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig { ExternalIpV6 = "2001:4860:4860::8888" },
            family => family == AddressFamily.InterNetwork
                ? [++sourceCalls == 1 ? SuccessfulSource("8.8.8.8") : Source(refreshStarted, refreshResult.Task)]
                : throw new InvalidOperationException("The configured IPv6 family must not be resolved."),
            timeProvider: timeProvider);
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ipResolver.Changed += (_, _) => changed.TrySetResult();

        IIPResolver.NethermindIp first = await ipResolver.Resolve();
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        Task<IIPResolver.NethermindIp> servedWhileRefreshing = ipResolver.Resolve().AsTask();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        IIPResolver.NethermindIp servedDuringRefresh = await ipResolver.Resolve();

        refreshResult.SetResult((true, IPAddress.Parse("8.8.4.4")));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        IIPResolver.NethermindIp refreshed = await ipResolver.Resolve();
        Assert.That(servedWhileRefreshing.IsCompletedSuccessfully, Is.True);
        IIPResolver.NethermindIp servedBeforeRefreshCompleted = await servedWhileRefreshing;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(servedBeforeRefreshCompleted, Is.EqualTo(first));
            Assert.That(servedDuringRefresh, Is.EqualTo(first));
            Assert.That(refreshed.ExternalIpV4, Is.EqualTo(IPAddress.Parse("8.8.4.4")));
            Assert.That(sourceCalls, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Wall_clock_rollback_does_not_postpone_refresh()
    {
        ManualTimeProvider timeProvider = new();
        int sourceCalls = 0;
        await using IPResolver ipResolver = CreateResolver(
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
        await using IPResolver ipResolver = CreateResolver(
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
        await using IPResolver ipResolver = CreateResolver(
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
        await using IPResolver ipResolver = CreateResolver(
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
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family == AddressFamily.InterNetwork
                ? throw new InvalidOperationException("Source factory failed.")
                : [SuccessfulSource("2001:4860:4860::8888")]);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIpV4, Is.Null);
            Assert.That(ip.ExternalIpV6, Is.EqualTo(IPAddress.Parse("2001:4860:4860::8888")));
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
        await using IPResolver ipResolver = CreateResolver(networkConfig);
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
        await using IPResolver ipResolver = CreateResolver(networkConfig);

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
        await using IPResolver ipResolver = CreateResolver(networkConfig);

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
        await using IPResolver ipResolver = CreateResolver(
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
    public async Task Unresolved_external_ip_is_warned_once_per_outage()
    {
        InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
        underlyingLogger.IsWarn.Returns(true);
        ILogger logger = new(underlyingLogger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<IPResolver>().Returns(logger);
        ManualTimeProvider timeProvider = new();
        Queue<(bool Success, IPAddress Ip)> results = new(
        [
            (false, IPAddress.None),
            (false, IPAddress.None),
            (true, IPAddress.Parse("8.8.8.8")),
            (false, IPAddress.None)
        ]);
        IIPSource source = new StubIpSource(() => Task.FromResult(results.Dequeue()));
        await using IPResolver ipResolver = CreateResolver(
            new NetworkConfig(),
            family => family == AddressFamily.InterNetwork ? [source] : [],
            logManager,
            timeProvider);

        Task<IIPResolver.NethermindIp> startup = ipResolver.Resolve().AsTask();
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromHours(2));
        await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.Empty);
            underlyingLogger.Received(2).Warn(Arg.Is<string>(message => message.Contains("External IP could not be resolved")));
        }
    }

    [Test]
    public async Task Can_resolve_local_ip_with_override()
    {
        const string ipOverride = "99.99.99.99";
        INetworkConfig networkConfig = new NetworkConfig { LocalIp = ipOverride, EnableExternalIpResolution = false };
        await using IPResolver ipResolver = CreateResolver(networkConfig);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve();
        Assert.That(ip.LocalIp, Is.EqualTo(IPAddress.Parse(ipOverride)));
    }

    private static IPResolver CreateResolver(
        INetworkConfig networkConfig,
        Func<AddressFamily, IEnumerable<IIPSource>>? externalIpSources = null,
        ILogManager? logManager = null,
        TimeProvider? timeProvider = null,
        Func<AddressFamily, bool>? hasLocalAddressFamily = null)
        => new(
            networkConfig,
            logManager ?? LimboLogs.Instance,
            externalIpSources ?? (_ => []),
            timeProvider,
            hasLocalAddressFamily ?? (_ => true));

    private static IContainer CreateContainer(IIPResolver resolver)
        => new ContainerBuilder()
            .AddModule(new TestNethermindModule(new DiscoveryConfig { DiscoveryVersion = DiscoveryVersion.V5 }))
            .AddSingleton(resolver)
            .Build();

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
        public Task<(bool Success, IPAddress Ip)> TryGetIP(CancellationToken cancellationToken = default) => resolve().WaitAsync(cancellationToken);
    }

    private sealed class BlockingIpSource(bool completeOnCancellation) : IIPSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }

        public async Task<(bool Success, IPAddress Ip)> TryGetIP(CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return (false, IPAddress.None);
            }
            catch (OperationCanceledException) when (completeOnCancellation)
            {
                return (true, IPAddress.Parse("8.8.4.4"));
            }
            finally
            {
                Cancelled = cancellationToken.IsCancellationRequested;
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private long _timestamp;
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan timeSpan)
        {
            _utcNow += timeSpan;
            _timestamp += timeSpan.Ticks;
            foreach (ManualTimer timer in _timers.ToArray())
            {
                timer.FireIfDue();
            }
        }

        public void AdjustUtc(TimeSpan timeSpan) => _utcNow += timeSpan;

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private long _dueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                return true;
            }

            public void FireIfDue()
            {
                if (!_disposed && clock.GetTimestamp() >= _dueAt)
                {
                    _dueAt = long.MaxValue;
                    callback(state);
                }
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
