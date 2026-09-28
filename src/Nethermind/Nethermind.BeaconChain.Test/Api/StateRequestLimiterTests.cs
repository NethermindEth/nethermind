// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// A node that serves checkpoint sync must bound the requests that load a whole beacon state:
/// beyond the bound they are refused at once, never queued, so memory stays bounded however many
/// clients ask.
/// </summary>
public class StateRequestLimiterTests
{
    private const string Octet = "application/octet-stream";
    private const string Json = "application/json";
    private const string Download = "/eth/v2/debug/beacon/states/head";
    private static readonly Hash256 Root = BeaconApiTestHost.TestRoot(0x50);
    private static readonly byte[] StateBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9];
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Test]
    public async Task State_requests_beyond_the_concurrency_bound_are_refused_with_503_while_the_bound_is_held()
    {
        TestMemColumnsDb<BeaconChainDbColumns> db = new();
        await using BeaconApiTestHost host = await StartWithStateAsync(db, new BeaconApiConfig { MaxConcurrentStateRequests = 1, StateDownloadsPerMinutePerClient = 2 });

        TestMemDb states = (TestMemDb)db.GetColumnDb(BeaconChainDbColumns.States);
        Dictionary<byte[], byte[]> stored = states.GetAll().ToDictionary(entry => entry.Key, entry => entry.Value, Bytes.EqualityComparer);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int reads = 0;
        states.ReadFunc = key =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.Set();
                release.Wait(Wait);
            }

            return stored.GetValueOrDefault(key)!;
        };

        Task<HttpResponseMessage> holder = host.GetAsync(Download, Octet);
        Assert.That(entered.Wait(Wait), Is.True, "the first request must reach the state read and hold its permit there");

        HttpResponseMessage[] refused = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(i => host.GetAsync(i % 2 == 0 ? Download : "/eth/v1/beacon/states/head/fork", i % 2 == 0 ? Octet : Json)));

        using (Assert.EnterMultipleScope())
        {
            foreach (HttpResponseMessage response in refused)
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), response.RequestMessage!.RequestUri!.ToString());
                using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
                Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo(503));
                Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("already serving 1 beacon state request"));
            }
        }

        Assert.That(reads, Is.EqualTo(1), "a refused request must not touch the state column");
        release.Set();
        HttpResponseMessage served = await holder;
        Assert.That(served.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await served.Content.ReadAsByteArrayAsync(), Is.EqualTo(StateBytes));

        HttpResponseMessage afterRelease = await host.GetAsync(Download, Octet);
        Assert.That(afterRelease.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the permit must be returned once a request completes, and a refused download must not spend the client's rate");
    }

    [Test]
    public async Task State_downloads_beyond_the_per_client_rate_are_refused_with_429_and_a_retry_after()
    {
        await using BeaconApiTestHost host = await StartWithStateAsync(new TestMemColumnsDb<BeaconChainDbColumns>(),
            new BeaconApiConfig { StateDownloadsPerMinutePerClient = 2 });

        for (int i = 0; i < 2; i++)
        {
            Assert.That((await host.GetAsync(Download, Octet)).StatusCode, Is.EqualTo(HttpStatusCode.OK), $"download {i + 1} is within the rate");
        }

        HttpResponseMessage limited = await host.GetAsync(Download, Octet);
        Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(limited);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo(429));
            Assert.That(limited.Headers.RetryAfter?.Delta, Is.GreaterThan(TimeSpan.Zero));
        }

        HttpResponseMessage otherStateRoute = await host.GetAsync("/eth/v1/beacon/states/head/fork", Json);
        Assert.That(otherStateRoute.StatusCode, Is.Not.EqualTo(HttpStatusCode.TooManyRequests), "the per-client rate covers state downloads only");
    }

    [TestCase(0, 30, TestName = "No concurrent state request allowed")]
    [TestCase(2, -1, TestName = "Negative download rate")]
    public void A_limit_out_of_range_fails_the_host_start(int maxConcurrent, int perMinute) =>
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet,
            apiConfig: new BeaconApiConfig { MaxConcurrentStateRequests = maxConcurrent, StateDownloadsPerMinutePerClient = perMinute }));

    [TestCase("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:ffff", true, TestName = "Addresses of one IPv6 /64 share a budget")]
    [TestCase("2001:db8:1:2::1", "2001:db8:1:3::1", false, TestName = "Adjacent IPv6 /64s do not")]
    [TestCase("::ffff:192.0.2.7", "192.0.2.7", true, TestName = "An IPv4-mapped IPv6 address is its IPv4 address")]
    [TestCase("192.0.2.7", "192.0.2.8", false, TestName = "Each IPv4 address has its own budget")]
    [TestCase("::", "0.0.0.0", false, TestName = "IPv6 and IPv4 keys never collide")]
    public void Clients_are_counted_per_IPv4_address_and_per_IPv6_64(string first, string second, bool shared) =>
        Assert.That(StateRequestLimiter.ClientKey(IPAddress.Parse(first)) == StateRequestLimiter.ClientKey(IPAddress.Parse(second)), Is.EqualTo(shared));

    [Test]
    public async Task A_client_with_a_state_request_in_flight_is_refused_a_second_while_other_clients_are_served()
    {
        using StateRequestLimiter limiter = new(new BeaconApiConfig { MaxConcurrentStateRequests = 3, StateDownloadsPerMinutePerClient = 0 });
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int served = 0;
        RequestDelegate hold = _ =>
        {
            Interlocked.Increment(ref served);
            return release.Task;
        };

        Task held = limiter.InvokeAsync(StateRequest(Download, "2001:db8::1"), hold);
        DefaultHttpContext sameClient = StateRequest("/eth/v1/beacon/states/head/validators", "2001:db8::2");
        await limiter.InvokeAsync(sameClient, _ =>
        {
            Interlocked.Increment(ref served);
            return Task.CompletedTask;
        });
        Task otherClient = limiter.InvokeAsync(StateRequest(Download, "192.0.2.7"), hold);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sameClient.Response.StatusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests), "one address of the same /64 is the same client");
            Assert.That(served, Is.EqualTo(2), "the holder and the other client reach the endpoint; the refused request does not");
        }

        release.SetResult();
        await Task.WhenAll(held, otherClient);
        DefaultHttpContext afterRelease = StateRequest(Download, "2001:db8::2");
        await limiter.InvokeAsync(afterRelease, _ => Task.CompletedTask);
        Assert.That(afterRelease.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK), "the client's permit is returned once its request completes");
    }

    [TestCase("/eth/v1/beacon/states/head/root", true)]
    [TestCase("/eth/v1/beacon/states/0x01/ROOT/", true)]
    [TestCase("/eth/v1/beacon/states/head/fork", false)]
    [TestCase("/eth/v1/beacon/states/root/fork", false)]
    [TestCase("/eth/v1/beacon/states/head/validators/root", false)]
    [TestCase("/eth/v2/debug/beacon/states/root", false)]
    public async Task Only_the_state_root_lookup_is_served_while_every_state_permit_is_held(string path, bool served)
    {
        using StateRequestLimiter limiter = new(new BeaconApiConfig { MaxConcurrentStateRequests = 1, StateDownloadsPerMinutePerClient = 0 });
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = limiter.InvokeAsync(StateRequest(Download, "192.0.2.7"), _ => release.Task);

        bool reached = false;
        DefaultHttpContext probe = StateRequest(path, "192.0.2.8");
        await limiter.InvokeAsync(probe, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        release.SetResult();
        await held;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reached, Is.EqualTo(served));
            Assert.That(probe.Response.StatusCode, Is.EqualTo(served ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
        }
    }

    private static DefaultHttpContext StateRequest(string path, string client)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(client);
        return context;
    }

    private static async Task<BeaconApiTestHost> StartWithStateAsync(TestMemColumnsDb<BeaconChainDbColumns> db, BeaconApiConfig config)
    {
        BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, db: db, apiConfig: config);
        host.SetStatus(Root, Root, 0);
        host.Store.PutState(Root, StateBytes);
        return host;
    }
}
