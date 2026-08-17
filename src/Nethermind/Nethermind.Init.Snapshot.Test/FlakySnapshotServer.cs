// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Net;

namespace Nethermind.Init.Snapshot.Test;

internal sealed class FlakySnapshotServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, int> _attemptsPerRange = new();
    private readonly CancellationTokenSource _hangCts = new();
    private int _requestCount;
    private int _hangConsumed;
    private int _switchAfterRequests = int.MaxValue;
    private byte[] _newContent = [];
    private string? _newETag;

    public FlakySnapshotServer()
    {
        (_listener, int port) = TestHttpListener.Start();
        Url = $"http://127.0.0.1:{port}/snapshot.tar.zst";
        Task.Run(AcceptLoopAsync);
    }

    public string Url { get; }

    public byte[] Content { get; set; } = [];

    public string? ETag { get; set; } = "\"v1\"";

    public bool SupportsRanges { get; set; } = true;

    public int? DropFirstAttemptPerRangeAfterBytes { get; set; }

    public int? FailWithNotFoundAfterRequests { get; set; }

    public bool OmitContentLength { get; set; }

    public bool RotateETagEveryRequest { get; set; }

    public int? HangOnceAfterBytes { get; set; }

    public int RequestCount => _requestCount;

    public void SwitchSourceAfterRequests(int requestCount, byte[] newContent, string? newETag)
    {
        _newContent = newContent;
        _newETag = newETag;
        _switchAfterRequests = requestCount;
    }

    public void Dispose()
    {
        _hangCts.Cancel();
        _listener.Stop();
        _listener.Close();
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        int requestNumber = Interlocked.Increment(ref _requestCount);
        byte[] content = requestNumber > _switchAfterRequests ? _newContent : Content;
        string? etag = requestNumber > _switchAfterRequests ? _newETag : ETag;
        if (RotateETagEveryRequest)
            etag = $"\"v{requestNumber}\"";
        HttpListenerResponse response = context.Response;

        try
        {
            if (FailWithNotFoundAfterRequests is int failAfter && requestNumber > failAfter)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            if (etag is not null)
                response.Headers["ETag"] = etag;
            string? rangeHeader = context.Request.Headers["Range"];
            string? ifRange = context.Request.Headers["If-Range"];
            long from = 0;
            long to = content.Length - 1;
            bool ranged = SupportsRanges
                          && rangeHeader is not null
                          && (ifRange is null || ifRange == etag)
                          && TryParseRange(rangeHeader, content.Length, ref from, ref to);

            if (ranged && from >= content.Length)
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{content.Length}";
                response.Close();
                return;
            }

            if (ranged)
            {
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes {from}-{to}/{content.Length}";
            }
            else
            {
                response.StatusCode = 200;
                from = 0;
                to = content.Length - 1;
            }

            long length = to - from + 1;
            if (OmitContentLength)
                response.SendChunked = true;
            else
                response.ContentLength64 = length;

            if (HangOnceAfterBytes is int hangAfter && length > hangAfter && Interlocked.Exchange(ref _hangConsumed, 1) == 0)
            {
                await response.OutputStream.WriteAsync(content.AsMemory((int)from, hangAfter));
                await response.OutputStream.FlushAsync();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _hangCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                response.Abort();
                return;
            }

            string rangeKey = rangeHeader ?? "full";
            int attempt = _attemptsPerRange.AddOrUpdate(rangeKey, 1, static (_, previous) => previous + 1);
            if (DropFirstAttemptPerRangeAfterBytes is int dropAfter && attempt == 1 && length > dropAfter)
            {
                await response.OutputStream.WriteAsync(content.AsMemory((int)from, dropAfter));
                response.Abort();
                return;
            }

            await response.OutputStream.WriteAsync(content.AsMemory((int)from, (int)length));
            response.Close();
        }
        catch
        {
            try
            {
                response.Abort();
            }
            catch
            {
            }
        }
    }

    private static bool TryParseRange(string rangeHeader, long contentLength, ref long from, ref long to)
    {
        if (!rangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
            return false;

        string[] parts = rangeHeader["bytes=".Length..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], out long start))
            return false;

        from = start;
        to = parts[1].Length > 0 && long.TryParse(parts[1], out long end)
            ? Math.Min(end, contentLength - 1)
            : contentLength - 1;
        return true;
    }

}
