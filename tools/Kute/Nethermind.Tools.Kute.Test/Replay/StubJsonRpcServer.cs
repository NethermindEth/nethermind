// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nethermind.Tools.Kute.Test.Replay;

/// <summary>A minimal keep-alive HTTP/1.1 endpoint that answers JSON-RPC posts.</summary>
/// <remarks>
/// Written on raw sockets rather than <see cref="HttpListener"/> so the tests need no URL reservation,
/// and so the requests the replay harness puts on the wire can be observed exactly as sent. Records
/// the peak number of requests in flight, which is what a concurrency sweep claims to control.
/// <para>
/// <c>releaseAt</c> turns that observation into a barrier: no request is answered until that many are
/// in flight at once. A harness holding fewer open can never satisfy it, so the assertion does not
/// depend on a response delay outlasting the scheduler.
/// </para>
/// </remarks>
public sealed class StubJsonRpcServer : IAsyncDisposable
{
    private static readonly byte[] HeaderTerminator = [(byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n'];

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<string> _bodies = [];
    private readonly Lock _bodiesLock = new();
    private readonly Func<string, (HttpStatusCode Status, string Body)> _responder;
    private readonly TimeSpan _delay;
    private readonly int _releaseAt;
    private readonly TaskCompletionSource _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _inFlight;
    private int _peakInFlight;
    private int _requests;
    private int _connections;

    /// <param name="responder">Maps a request body to the status and body of its response.</param>
    /// <param name="delay">Time each request is held before being answered.</param>
    /// <param name="releaseAt">
    /// Number of simultaneously in-flight requests that must accumulate before any is answered;
    /// <c>0</c> answers immediately.
    /// </param>
    public StubJsonRpcServer(
        Func<string, (HttpStatusCode Status, string Body)>? responder = null,
        TimeSpan delay = default,
        int releaseAt = 0)
    {
        _responder = responder ?? (_ => (HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":"0x1"}"""));
        _delay = delay;
        _releaseAt = releaseAt;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Endpoint to point the harness at.</summary>
    public Uri Address => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    /// <summary>Requests answered so far.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Highest number of requests in flight at once.</summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>Connections accepted so far.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Bodies of every request received, in the order they were fully read.</summary>
    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_bodiesLock)
            {
                return [.. _bodies];
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        List<Task> connections = [];
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket socket = await _listener.AcceptSocketAsync(token);
                Interlocked.Increment(ref _connections);
                connections.Add(ServeAsync(socket, token));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        await Task.WhenAll(connections);
    }

    private async Task ServeAsync(Socket socket, CancellationToken token)
    {
        using NetworkStream stream = new(socket, ownsSocket: true);
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? body = await ReadRequestAsync(stream, token);
                if (body is null)
                {
                    return;
                }

                int inFlight = Interlocked.Increment(ref _inFlight);
                UpdatePeak(inFlight);

                lock (_bodiesLock)
                {
                    _bodies.Add(body);
                }

                await WaitForBarrierAsync(inFlight, token);

                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, token);
                }

                (HttpStatusCode status, string responseBody) = _responder(body);
                await WriteResponseAsync(stream, status, responseBody, token);

                Interlocked.Decrement(ref _inFlight);
                Interlocked.Increment(ref _requests);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Holds a request until enough others join it, so peak concurrency is observed exactly.</summary>
    /// <remarks>
    /// Gives up after a few seconds rather than hanging: the waiting test then fails on its peak
    /// assertion, which says what went wrong, instead of timing out with no diagnosis.
    /// </remarks>
    private async Task WaitForBarrierAsync(int inFlight, CancellationToken token)
    {
        if (_releaseAt <= 0)
        {
            return;
        }

        if (inFlight >= _releaseAt)
        {
            _barrier.TrySetResult();
        }

        try
        {
            await _barrier.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        }
        catch (TimeoutException)
        {
        }
    }

    private void UpdatePeak(int inFlight)
    {
        int peak = Volatile.Read(ref _peakInFlight);
        while (inFlight > peak)
        {
            int seen = Interlocked.CompareExchange(ref _peakInFlight, inFlight, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }

    /// <summary>Reads one request, returning its body, or <see langword="null"/> if the peer closed.</summary>
    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        List<byte> head = new(512);
        byte[] one = new byte[1];
        int matched = 0;

        while (matched < HeaderTerminator.Length)
        {
            int read = await stream.ReadAsync(one, token);
            if (read == 0)
            {
                return null;
            }

            head.Add(one[0]);
            matched = one[0] == HeaderTerminator[matched] ? matched + 1 : 0;
        }

        string headers = Encoding.ASCII.GetString([.. head]);
        int contentLength = ParseContentLength(headers);
        if (contentLength == 0)
        {
            return string.Empty;
        }

        byte[] body = new byte[contentLength];
        await stream.ReadExactlyAsync(body, token);

        return Encoding.UTF8.GetString(body);
    }

    private static int ParseContentLength(string headers)
    {
        foreach (string line in headers.Split("\r\n"))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
        }

        throw new InvalidDataException($"Request has no Content-Length, so the harness did not size its body: {headers}");
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpStatusCode status, string body, CancellationToken token)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {(int)status} {status}\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {payload.Length}\r\n"
            + "Connection: keep-alive\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();

        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
