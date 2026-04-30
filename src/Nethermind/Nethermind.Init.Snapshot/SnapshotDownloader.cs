// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Nethermind.Core.Timers;
using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

/// <summary>
/// Downloads a snapshot file from a URL with resumable download support.
/// Manually follows HTTP redirects to preserve the Range header, which standard
/// HttpClient strips on auto-redirect.
/// </summary>
internal sealed class SnapshotDownloader(ILogManager logManager, ITimerFactory timerFactory) : IDisposable
{
    private const int BufferSize = 65536;
    private const int MaxRedirects = 10;
    private const int ResumeWarningDelaySeconds = 5;
    private const int ProgressIntervalSeconds = 5;

    // A single HttpClient is shared for all retries to preserve the connection pool.
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly ILogger _logger = logManager.GetClassLogger<SnapshotDownloader>();

    /// <summary>
    /// Downloads the snapshot to <paramref name="destinationPath"/>, resuming from the
    /// existing file size if a partial download is already present. When the server
    /// honors the Range header (HTTP 206) the remaining bytes are appended directly.
    /// When the server returns HTTP 200 with an existing partial file, the already
    /// downloaded prefix is consumed from the response stream and the rest is appended,
    /// avoiding a full re-download.
    /// </summary>
    public async Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        FileInfo file = new(destinationPath);
        file.Refresh();
        long existingSize = file.Exists ? file.Length : 0;

        if (_logger.IsInfo)
            _logger.Info($"Downloading snapshot from {url} to {file.FullName}");

        if (existingSize > 0)
        {
            if (_logger.IsWarn)
                _logger.Warn("Snapshot file already exists. Resuming download. To interrupt press Ctrl^C");
            await Task.Delay(TimeSpan.FromSeconds(ResumeWarningDelaySeconds), cancellationToken).ConfigureAwait(false);
        }

        using HttpResponseMessage response = await SendWithRangeAsync(_httpClient, url, existingSize, cancellationToken).ConfigureAwait(false);

        if (_logger.IsInfo)
            _logger.Info($"Server response: {response.StatusCode}, ETag: {response.Headers.ETag}, Last-Modified: {response.Content.Headers.LastModified}");

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (_logger.IsInfo)
                _logger.Info("Snapshot file already fully downloaded (server returned 416).");
            return;
        }

        (FileMode fileMode, long bytesToSkip, long? totalSize) = ResolveCopyStrategy(response.StatusCode, existingSize, response.Content.Headers.ContentLength);

        if (bytesToSkip > 0 && _logger.IsWarn)
            _logger.Warn($"Server does not support range requests. Consuming {bytesToSkip} already-downloaded bytes to resume.");

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream fileStream = new(destinationPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        long initialProgress = fileMode == FileMode.Append ? existingSize : 0;
        using ProgressTracker progressTracker = new(logManager, timerFactory, TimeSpan.FromSeconds(ProgressIntervalSeconds), initialProgress, totalSize);

        if (bytesToSkip > 0)
            await SkipBytesAsync(contentStream, bytesToSkip, cancellationToken).ConfigureAwait(false);

        await CopyWithProgressAsync(contentStream, fileStream, progressTracker, cancellationToken).ConfigureAwait(false);

        if (_logger.IsInfo)
            _logger.Info($"Snapshot downloaded to {destinationPath}.");
    }

    public void Dispose() => _httpClient.Dispose();

    private static (FileMode fileMode, long bytesToSkip, long? totalSize) ResolveCopyStrategy(
        HttpStatusCode statusCode, long existingSize, long? contentLength) =>
        statusCode switch
        {
            // Server honored the Range request — append the remaining bytes.
            HttpStatusCode.PartialContent => (FileMode.Append, 0L, existingSize + contentLength),
            // Server returned the full file but a partial download exists — skip the
            // already-downloaded prefix in the stream and append the remainder.
            HttpStatusCode.OK when existingSize > 0 => (FileMode.Append, existingSize, contentLength),
            // Server returned the full file from scratch — create or overwrite.
            HttpStatusCode.OK => (FileMode.Create, 0L, contentLength),
            _ => throw new IOException($"Unexpected HTTP status: {statusCode}")
        };

    private static async Task SkipBytesAsync(Stream stream, long bytesToSkip, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long remaining = bytesToSkip;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(buffer.Length, remaining);
                await stream.ReadAtLeastAsync(buffer.AsMemory(0, chunk), chunk, throwOnEndOfStream: true, cancellationToken).ConfigureAwait(false);
                remaining -= chunk;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<HttpResponseMessage> SendWithRangeAsync(
        HttpClient httpClient, string url, long existingSize, CancellationToken cancellationToken)
    {
        Uri currentUri = new(url);

        for (int redirects = 0; redirects < MaxRedirects; redirects++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            if (existingSize > 0)
                request.Headers.Range = new RangeHeaderValue(existingSize, null);

            HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect:
                    {
                        Uri? location = response.Headers.Location;
                        response.Dispose();
                        if (location is null)
                            throw new IOException("Redirect response missing Location header.");
                        currentUri = new Uri(currentUri, location); // resolve relative redirects
                        continue;
                    }
                // Let the caller handle 416 — it means the file is already complete.
                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    return response;
                default:
                    return response.EnsureSuccessStatusCode();
            }
        }

        throw new IOException("Too many redirects while downloading snapshot.");
    }

    private static async Task CopyWithProgressAsync(
        Stream source, FileStream destination, ProgressTracker tracker, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                tracker.AddProgress(bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
