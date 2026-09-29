// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>How a <see cref="FlakyCheckpointProvider"/> answers one request for the finalized state.</summary>
internal enum StateResponse
{
    Serve,
    /// <summary>Promises the whole body, sends half and resets the connection.</summary>
    DropMidBody,
    ServerError,
    NotFound,
}

/// <summary>A beacon API serving <see cref="ForkCrossingChain.First"/> whose state endpoint answers each request as <c>responseFor</c> says, counting the requests.</summary>
internal sealed class FlakyCheckpointProvider : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _stateRequests;
    private int _blockRequests;

    private FlakyCheckpointProvider(WebApplication app) => _app = app;

    public string Url => _app.Urls.First();

    public int StateRequests => Volatile.Read(ref _stateRequests);

    public int BlockRequests => Volatile.Read(ref _blockRequests);

    /// <param name="responseFor">Maps the 1-based number of a state request to its answer.</param>
    /// <param name="state">The state served, <see cref="ForkCrossingChain.First"/>'s post-state when omitted; the block is always the one of that chain.</param>
    /// <param name="consensusVersion">The <c>Eth-Consensus-Version</c> header of a served state, none when omitted.</param>
    /// <param name="blockResponseFor">Maps the 1-based number of a request for the anchor block to its answer, always served when omitted.</param>
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
        static async Task Answer(HttpContext c, StateResponse response, byte[] body, string? consensusVersion)
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
                case StateResponse.ServerError:
                    c.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
                case StateResponse.NotFound:
                    c.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        }

        app.MapGet("/eth/v2/debug/beacon/states/finalized", (HttpContext c) =>
            Answer(c, responseFor(Interlocked.Increment(ref provider._stateRequests)), stateSsz, consensusVersion));
        app.MapGet("/eth/v2/beacon/blocks/{root}", (HttpContext c, string root) =>
        {
            if (root != first.Root.ToString())
            {
                c.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            int request = Interlocked.Increment(ref provider._blockRequests);
            return Answer(c, blockResponseFor?.Invoke(request) ?? StateResponse.Serve, blockSsz, null);
        });
        await app.StartAsync();
        return provider;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>A log manager recording every line with its level, for asserting what an operator would read.</summary>
internal sealed class LevelCapturingLogManager : ILogManager
{
    private readonly ConcurrentQueue<(string Level, string Text)> _lines = new();

    public IReadOnlyCollection<(string Level, string Text)> Lines => _lines;

    public Nethermind.Logging.ILogger GetLogger(string loggerName) => new(new Capture(_lines));

    public Nethermind.Logging.ILogger GetClassLogger<T>() => GetLogger(typeof(T).Name);

    private sealed class Capture(ConcurrentQueue<(string Level, string Text)> lines) : InterfaceLogger
    {
        public bool IsInfo => true;
        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsTrace => true;
        public bool IsError => true;

        public void Info(string text) => lines.Enqueue(("Info", text));
        public void Warn(string text) => lines.Enqueue(("Warn", text));
        public void Debug(string text) => lines.Enqueue(("Debug", text));
        public void Trace(string text) => lines.Enqueue(("Trace", text));
        public void Error(string text, Exception? ex = null) => lines.Enqueue(("Error", text));
    }
}
