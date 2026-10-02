// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.BeaconChain.P2P.ReqResp;

/// <summary>How far one outbound request got and when: its channel opened, its first chunk arrived, how many chunks it read.</summary>
/// <remarks>Networking req/resp requesting side: a cancelled dial retains its concurrency permit until the protocol ends.</remarks>
public sealed class RequestTiming
{
    private static readonly ConditionalWeakTable<object, RequestTiming> ByRequest = [];

    private readonly Lock _lock = new();
    private long _startedAt = Stopwatch.GetTimestamp();
    private object? _tracked;
    private long _channelOpenedAt;
    private long _firstChunkAt;
    private int _chunks;
    private volatile bool _measured;
    private int _running;
    private bool _settling;
    private TaskCompletionSource? _drained;

    internal RequestTiming()
    {
    }

    /// <summary>Lets the protocol that receives <paramref name="request"/> report on this timing.</summary>
    /// <remarks>The argument must be this request's own object: a timing tracked through a shared one would take another request's progress.</remarks>
    internal T Track<T>(T request) where T : class
    {
        ByRequest.AddOrUpdate(request, this);
        lock (_lock)
        {
            _tracked = request;
        }

        _measured = true;
        return request;
    }

    /// <summary>Called by a protocol as its dial starts; the exchange lasts until the result is disposed.</summary>
    /// <exception cref="OperationCanceledException">The requester already gave up on the request, so its channel must not carry it.</exception>
    internal static Exchange Open(object? request)
    {
        if (request is null || !ByRequest.TryGetValue(request, out RequestTiming? timing))
        {
            return default;
        }

        lock (timing._lock)
        {
            if (timing._settling)
            {
                throw new OperationCanceledException("The requester gave up on the request before its channel opened");
            }

            // An attempt the requester abandoned for a new one must not send, however late its channel opens.
            if (!ReferenceEquals(timing._tracked, request))
            {
                throw new OperationCanceledException("The requester abandoned this attempt before its channel opened");
            }

            timing._running++;
            Interlocked.CompareExchange(ref timing._channelOpenedAt, Stopwatch.GetTimestamp(), 0);
        }

        return new Exchange(timing);
    }

    /// <summary>Completes once no exchange of this request is running, and from then on refuses to start one.</summary>
    internal Task Settled()
    {
        lock (_lock)
        {
            _settling = true;
            return _running == 0 ? Task.CompletedTask : (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    /// <summary>Gives up the current attempt unless its channel already opened; from then on its protocol cannot open (see <see cref="Open"/>).</summary>
    /// <returns><c>false</c> when the channel had opened, so the attempt goes on.</returns>
    internal bool TryAbandon()
    {
        lock (_lock)
        {
            if (Volatile.Read(ref _channelOpenedAt) != 0)
            {
                return false;
            }

            _tracked = null;
            return true;
        }
    }

    /// <summary>Starts the timing again for a new attempt at the same request, which must then be tracked again with a new object.</summary>
    /// <remarks>The earlier attempt's object can no longer open (see <see cref="Open"/>), so an abandoned attempt that opens late cannot send or mark the new one opened.</remarks>
    internal void Restart()
    {
        lock (_lock)
        {
            _tracked = null;
            Volatile.Write(ref _channelOpenedAt, 0);
        }

        Volatile.Write(ref _firstChunkAt, 0);
        Volatile.Write(ref _chunks, 0);
        Volatile.Write(ref _startedAt, Stopwatch.GetTimestamp());
    }

    internal void ChunkRead()
    {
        if (Interlocked.Increment(ref _chunks) == 1)
        {
            Interlocked.CompareExchange(ref _firstChunkAt, Stopwatch.GetTimestamp(), 0);
        }
    }

    private void Ended()
    {
        TaskCompletionSource? drained;
        lock (_lock)
        {
            if (--_running > 0 || _drained is null)
            {
                return;
            }

            drained = _drained;
        }

        drained.TrySetResult();
    }

    /// <summary>The number of response chunks read.</summary>
    public int Chunks => Volatile.Read(ref _chunks);

    /// <summary>Whether the request's channel opened; <c>null</c> when its protocol cannot report.</summary>
    public bool? ChannelOpened => _measured ? Volatile.Read(ref _channelOpenedAt) != 0 : null;

    /// <summary>The time since the current request attempt began.</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(Volatile.Read(ref _startedAt));

    /// <summary>What the request was waiting for when it ended; empty when its protocol cannot report.</summary>
    public string WaitingFor =>
        !_measured ? ""
        : Volatile.Read(ref _channelOpenedAt) == 0 ? "waiting for the channel to open"
        : Chunks == 0 ? "waiting for the first chunk"
        : $"reading chunk {Chunks + 1}";

    /// <summary>The timing as the per-request Debug line shows it.</summary>
    public override string ToString() =>
        $"channel open {Since(Volatile.Read(ref _channelOpenedAt))}, first chunk {Since(Volatile.Read(ref _firstChunkAt))}, total {Elapsed.TotalMilliseconds:F0} ms, {Chunks} chunks";

    private string Since(long at) =>
        !_measured ? "not measured"
        : at == 0 ? "not reached"
        : $"{Stopwatch.GetElapsedTime(Volatile.Read(ref _startedAt), at).TotalMilliseconds:F0} ms";

    /// <summary>One protocol dial of a request, from its start to its end; <see cref="Timing"/> is <c>null</c> for an untimed request.</summary>
    internal readonly struct Exchange(RequestTiming? timing) : IDisposable
    {
        public RequestTiming? Timing => timing;

        public void Dispose() => timing?.Ended();
    }
}
