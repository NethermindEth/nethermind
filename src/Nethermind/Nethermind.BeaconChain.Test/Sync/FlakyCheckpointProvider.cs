// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Test.Sync;

internal enum StateResponse
{
    Serve,
    DropMidBody,
    StallMidBody,
    Trickle,
    NoHeaders,
    ServerError,
    NotFound,
}

internal sealed class FlakyCheckpointProvider : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<byte[]> _stallEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stateRequests;
    private int _blockRequests;

    private FlakyCheckpointProvider(WebApplication app) => _app = app;
    public string Url => _app.Urls.First();
    public int StateRequests => Volatile.Read(ref _stateRequests);
    public int BlockRequests => Volatile.Read(ref _blockRequests);
    public static TimeSpan TrickleInterval { get; } = TimeSpan.FromMilliseconds(250);
    public static TimeSpan TrickleDuration { get; } = TimeSpan.FromSeconds(5);
    public Task<byte[]> StallEntered => _stallEntered.Task;

    public static async Task<FlakyCheckpointProvider> StartAsync(Func<int, StateResponse> responseFor, BeaconStateGloas? state = null, string? consensusVersion = null, Func<int, StateResponse>? blockResponseFor = null)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        byte[] stateSsz = BeaconStateGloas.Encode(state ?? first.PostState);
        byte[] blockSsz = SignedBeaconBlockCodec.Encode(new ForkedSignedBeaconBlock.OfGloas(first.Block), GloasCheckpointFiles.Spec);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        FlakyCheckpointProvider provider = new(app);
        static async Task Hold(HttpContext c, CancellationToken stopping)
        {
            using CancellationTokenSource held = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, stopping);
            try
            {
                await Task.Delay(Timeout.Infinite, held.Token);
            }
            catch (OperationCanceledException)
            {
            }

            c.Abort();
        }

        async Task Answer(HttpContext c, StateResponse response, byte[] body, string? consensusVersion, CancellationToken stopping)
        {
            switch (response)
            {
                case StateResponse.Serve:
                    c.Response.ContentType = "application/octet-stream";
                    if (consensusVersion is not null)
                    {
                        c.Response.Headers["Eth-Consensus-Version"] = consensusVersion;
                    }

                    await c.Response.Body.WriteAsync(body);
                    break;
                case StateResponse.DropMidBody:
                    c.Response.ContentType = "application/octet-stream";
                    c.Response.ContentLength = body.Length;
                    await c.Response.Body.WriteAsync(body.AsMemory(0, body.Length / 2));
                    await c.Response.Body.FlushAsync();
                    c.Abort();
                    break;
                case StateResponse.StallMidBody:
                    c.Response.ContentType = "application/octet-stream";
                    c.Response.ContentLength = body.Length;
                    await c.Response.Body.WriteAsync(body.AsMemory(0, body.Length / 2));
                    await c.Response.Body.FlushAsync();
                    provider._stallEntered.TrySetResult(body[..(body.Length / 2)]);
                    await Hold(c, stopping);
                    break;
                case StateResponse.Trickle:
                    c.Response.ContentType = "application/octet-stream";
                    c.Response.ContentLength = body.Length;
                    int chunks = (int)(TrickleDuration / TrickleInterval);
                    int chunkLength = (body.Length + chunks - 1) / chunks;
                    for (int offset = 0; offset < body.Length; offset += chunkLength)
                    {
                        await c.Response.Body.WriteAsync(body.AsMemory(offset, Math.Min(chunkLength, body.Length - offset)), stopping);
                        await c.Response.Body.FlushAsync(stopping);
                        await Task.Delay(TrickleInterval, stopping);
                    }

                    break;
                case StateResponse.NoHeaders:
                    await Hold(c, stopping);
                    break;
                case StateResponse.ServerError:
                    c.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
                case StateResponse.NotFound:
                    c.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        }

        app.MapGet("/eth/v2/debug/beacon/states/finalized", (HttpContext c) =>
            Answer(c, responseFor(Interlocked.Increment(ref provider._stateRequests)), stateSsz, consensusVersion, provider._stopping.Token));
        app.MapGet("/eth/v2/beacon/blocks/{root}", (HttpContext c, string root) =>
        {
            if (root != first.Root.ToString())
            {
                c.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            int request = Interlocked.Increment(ref provider._blockRequests);
            return Answer(c, blockResponseFor?.Invoke(request) ?? StateResponse.Serve, blockSsz, null, provider._stopping.Token);
        });
        await app.StartAsync();
        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _app.DisposeAsync();
        _stopping.Dispose();
    }
}
