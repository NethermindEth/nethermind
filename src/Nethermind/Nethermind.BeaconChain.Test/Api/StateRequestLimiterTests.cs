// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;

namespace Nethermind.BeaconChain.Test.Api;

public class StateRequestLimiterTests
{
    private const string Octet = "application/octet-stream";
    private const string Json = "application/json";
    private const string Download = "/eth/v2/debug/beacon/states/head";
    private static readonly Hash256 Root = BeaconApiTestHost.TestRoot(0x50);
    private static readonly byte[] StateBytes = CreateStateBytes();
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

    [TestCase(2, "192.0.2.7", TestName = "Two requests per client allowed")]
    [TestCase(1, "127.0.0.1", TestName = "Loopback is limited like any other address")]
    [TestCase(1, "::1", TestName = "IPv6 loopback is limited like any other address")]
    public async Task The_configured_per_client_limit_is_the_number_of_state_requests_one_client_may_hold(int perClient, string client)
    {
        using StateRequestLimiter limiter = new(new BeaconApiConfig { MaxConcurrentStateRequests = 4, StateDownloadsPerMinutePerClient = 0, MaxConcurrentStateRequestsPerClient = perClient });
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reached = 0;
        Task[] held = Enumerable.Range(0, perClient).Select(_ => limiter.InvokeAsync(StateRequest(Download, client), _ =>
        {
            Interlocked.Increment(ref reached);
            return release.Task;
        })).ToArray();
        Assert.That(reached, Is.EqualTo(perClient), "every request within the limit must reach the endpoint");

        DefaultHttpContext over = StateRequest(Download, client);
        await limiter.InvokeAsync(over, _ => Task.CompletedTask);
        Assert.That(over.Response.StatusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests), $"request {perClient + 1} from one client exceeds its limit of {perClient}");

        release.SetResult();
        await Task.WhenAll(held);
        DefaultHttpContext afterRelease = StateRequest(Download, client);
        await limiter.InvokeAsync(afterRelease, _ => Task.CompletedTask);
        Assert.That(afterRelease.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK), "a released permit admits the next request");
    }

    [TestCase(1, null, null, true, TestName = "A_response_unfinished_at_the_deadline_is_aborted_and_its_permits_are_released")]
    [TestCase(40, 1, 0, true, TestName = "A_response_that_stops_being_written_is_cut_at_the_idle_bound_however_generous_the_total_cap")]
    [TestCase(40, 1, Timeout.Infinite, false, TestName = "A_first_write_that_never_completes_is_cut_at_the_idle_bound_however_generous_the_total_cap")]
    [TestCase(1, 3600, null, false, TestName = "A_response_never_written_is_cut_at_the_total_cap")]
    public async Task An_unfinished_response_observes_its_deadline_and_releases_its_permits(
        int totalSeconds, int? idleSeconds, int? writeDelayMilliseconds, bool verifyRelease)
    {
        BeaconApiConfig config = new() { StateDownloadsPerMinutePerClient = 0, StateResponseTimeoutSeconds = totalSeconds };
        if (idleSeconds is { } idle) config.StateResponseIdleTimeoutSeconds = idle;
        if (verifyRelease) config.MaxConcurrentStateRequests = 1;
        using StateRequestLimiter limiter = new(config);
        DefaultHttpContext request = StateRequest(Download, "192.0.2.7");
        if (writeDelayMilliseconds is { } delay) request.Response.Body = new PacedStream(TimeSpan.FromMilliseconds(delay));

        await limiter.InvokeAsync(request, async c =>
        {
            if (writeDelayMilliseconds is not null) await c.Response.Body.WriteAsync(new byte[16], c.RequestAborted);
            if (writeDelayMilliseconds is null or >= 0) await Task.Delay(Timeout.Infinite, c.RequestAborted);
        }).WaitAsync(Wait);

        bool reached = false;
        if (verifyRelease)
        {
            await limiter.InvokeAsync(StateRequest(Download, "192.0.2.7"), _ =>
            {
                reached = true;
                return Task.CompletedTask;
            });
        }
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(request.RequestAborted.IsCancellationRequested, Is.True, "the handler must see the configured deadline through RequestAborted");
        if (verifyRelease) Assert.That(reached, Is.True, "both the node-wide and the per-client permit must be free after the deadline");
    }

    [Test]
    public async Task A_slow_but_steady_download_completes_past_the_idle_bound_even_when_one_write_is_larger_than_it_can_send_in_that_time(
        [Values(16 * 1024, 256 * 1024)] int writeBytes)
    {
        using StateRequestLimiter limiter = new(new BeaconApiConfig { StateDownloadsPerMinutePerClient = 0, StateResponseTimeoutSeconds = 40, StateResponseIdleTimeoutSeconds = 1 });
        using PacedStream reader = new(TimeSpan.FromMilliseconds(100), 16 * 1024);
        DefaultHttpContext download = StateRequest(Download, "192.0.2.7");
        download.Response.Body = reader;
        byte[] state = new byte[writeBytes];

        await limiter.InvokeAsync(download, c => c.Response.Body.WriteAsync(state, c.RequestAborted).AsTask()).WaitAsync(Wait);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reader.Written, Is.EqualTo(state.Length), "every byte reaches a reader that never stalls longer than the idle bound between chunks");
        Assert.That(download.RequestAborted.IsCancellationRequested, Is.False, "steady writes must complete even when draining 256 KiB takes 1.6 s and the idle bound is 1 s");
    }

    [Test]
    public async Task A_state_that_takes_longer_than_the_idle_bound_to_load_is_not_cut_before_its_first_write()
    {
        using StateRequestLimiter limiter = new(new BeaconApiConfig { StateDownloadsPerMinutePerClient = 0, StateResponseTimeoutSeconds = 40, StateResponseIdleTimeoutSeconds = 1 });
        PacedStream reader = new(TimeSpan.Zero);
        DefaultHttpContext download = StateRequest(Download, "192.0.2.7");
        download.Response.Body = reader;

        await limiter.InvokeAsync(download, async c =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1600), c.RequestAborted);
            await c.Response.Body.WriteAsync(new byte[16], c.RequestAborted);
        }).WaitAsync(Wait);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reader.Written, Is.EqualTo(16), "loading a state is the node's own time, not a stalled reader, so only the total cap bounds it");
        Assert.That(download.RequestAborted.IsCancellationRequested, Is.False);
    }

    [TestCase(1, 120, TestName = "A client that stops reading a state cannot hold its permit past the total cap")]
    [TestCase(40, 1, TestName = "A client that stops reading a state cannot hold its permit past the idle bound")]
    public async Task A_client_that_stops_reading_a_state_cannot_hold_its_permit_past_the_deadline(int totalSeconds, int idleSeconds)
    {
        using StateRequestLimiter limiter = new(new BeaconApiConfig { StateDownloadsPerMinutePerClient = 0, StateResponseTimeoutSeconds = totalSeconds, StateResponseIdleTimeoutSeconds = idleSeconds });
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication app = builder.Build();
        app.Use(limiter.InvokeAsync);
        using SemaphoreSlim started = new(0);
        app.MapGet(Download, async c =>
        {
            started.Release();
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                await c.Response.Body.WriteAsync(chunk, c.RequestAborted);
                await c.Response.Body.FlushAsync(c.RequestAborted);
            }
        });
        await app.StartAsync();
        int port = new Uri(app.Urls.First()).Port;

        using TcpClient slowReader = new() { ReceiveBufferSize = 1024 };
        await slowReader.ConnectAsync(IPAddress.Loopback, port);
        await slowReader.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET {Download} HTTP/1.1\r\nHost: localhost\r\nAccept: {Octet}\r\n\r\n"));
        Assert.That(await started.WaitAsync(Wait), Is.True, "the slow reader's request must reach the endpoint");

        using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await Task.Delay(200);
        using (HttpResponseMessage held = await client.GetAsync(Download, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.That(held.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests), "the stalled response must hold the client's permit before the deadline");
        }

        // The server's own minimum response rate would end the stall after its 5 s grace period; the configured deadline must come first.
        DateTime giveUp = DateTime.UtcNow.AddSeconds(4);
        HttpStatusCode status;
        do
        {
            await Task.Delay(100);
            using HttpResponseMessage probe = await client.GetAsync(Download, HttpCompletionOption.ResponseHeadersRead);
            status = probe.StatusCode;
        }
        while (status == HttpStatusCode.TooManyRequests && DateTime.UtcNow < giveUp);

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "the stalled response's permit must be released at the deadline");
    }

    [Test]
    public async Task A_state_cut_off_at_the_deadline_reaches_the_client_as_a_failed_read_not_a_complete_response()
    {
        using StateRequestLimiter limiter = new(new BeaconApiConfig { StateDownloadsPerMinutePerClient = 0, StateResponseTimeoutSeconds = 1 });
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication app = builder.Build();
        app.Use(limiter.InvokeAsync);
        app.MapGet(Download, async c =>
        {
            await c.Response.Body.WriteAsync(new byte[64 * 1024], c.RequestAborted);
            await c.Response.Body.FlushAsync(c.RequestAborted);
            await Task.Delay(Timeout.Infinite, c.RequestAborted);
        });
        await app.StartAsync();

        using HttpClient client = new() { BaseAddress = new Uri(app.Urls.First()) };
        Assert.ThrowsAsync<HttpRequestException>(async () => await client.GetByteArrayAsync(Download).WaitAsync(Wait),
            "a truncated state must not be served as a complete 200");
    }

    [Test]
    public void A_client_abort_reaches_the_host_instead_of_being_taken_for_the_deadline()
    {
        using StateRequestLimiter limiter = new(new BeaconApiConfig { StateDownloadsPerMinutePerClient = 0, StateResponseTimeoutSeconds = 600 });
        using CancellationTokenSource clientGone = new();
        DefaultHttpContext request = StateRequest(Download, "192.0.2.7");
        request.RequestAborted = clientGone.Token;
        Task invocation = limiter.InvokeAsync(request, c => Task.Delay(Timeout.Infinite, c.RequestAborted));

        clientGone.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await invocation.WaitAsync(Wait));
    }

    [Test]
    public void An_idle_bound_out_of_range_fails_the_host_start([Values(0, StateRequestLimiter.MaxResponseTimeoutSeconds + 1)] int idleSeconds) =>
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet,
            apiConfig: new BeaconApiConfig { StateResponseIdleTimeoutSeconds = idleSeconds }));

    /// <summary>
    /// A reader just fast enough to download the smallest state within the total cap takes total / 40 s to accept one chunk, so a
    /// shorter idle bound would cut a steady download the total cap allows.
    /// </summary>
    [TestCase(3600, 89, false)]
    [TestCase(3600, 90, true)]
    [TestCase(41, 1, false)]
    [TestCase(40, 1, true)]
    public void An_idle_bound_shorter_than_one_chunk_at_the_slowest_finishing_rate_is_refused(int totalSeconds, int idleSeconds, bool accepted)
    {
        BeaconApiConfig config = new() { StateResponseTimeoutSeconds = totalSeconds, StateResponseIdleTimeoutSeconds = idleSeconds };

        Assert.That(() => new StateRequestLimiter(config).Dispose(), accepted
            ? Throws.Nothing
            : Throws.TypeOf<ArgumentOutOfRangeException>().With.Message.Contains($"{nameof(IBeaconApiConfig.StateResponseIdleTimeoutSeconds)} must be at least {(totalSeconds + 39) / 40}"));
    }

    [TestCase(0, 600, TestName = "No state request per client allowed")]
    [TestCase(1, 0, TestName = "No time to answer a state request")]
    [TestCase(1, StateRequestLimiter.MaxResponseTimeoutSeconds + 1, TestName = "A deadline CancelAfter cannot schedule")]
    public void A_per_client_limit_or_deadline_out_of_range_fails_the_host_start(int perClient, int timeoutSeconds) =>
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet,
            apiConfig: new BeaconApiConfig { MaxConcurrentStateRequestsPerClient = perClient, StateResponseTimeoutSeconds = timeoutSeconds }));

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reached, Is.EqualTo(served));
        Assert.That(probe.Response.StatusCode, Is.EqualTo(served ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
    }

    private sealed class PacedStream(TimeSpan perChunk, int bytesPerChunk = StateRequestLimiter.WriteChunkBytes) : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            TimeSpan delay = perChunk == Timeout.InfiniteTimeSpan ? perChunk : perChunk * ((double)buffer.Length / bytesPerChunk);
            await Task.Delay(delay, cancellationToken);
            Written += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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

    private static byte[] CreateStateBytes()
    {
        byte[] bytes = new byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), BeaconChainSpec.Mainnet.FuluForkEpoch * BeaconChainSpec.Mainnet.SlotsPerEpoch);
        return bytes;
    }
}
