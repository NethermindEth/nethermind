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
internal sealed class SnapshotDownloader(ILogManager logManager, ITimerFactory timerFactory)
{
    private const int BufferSize = 65536;
    private const int MaxRedirects = 10;

    private readonly ILogger _logger = logManager.GetClassLogger();

    /// <summary>
    /// Downloads the snapshot to <paramref name="destinationPath"/>, resuming from the
    /// existing file size if a partial download is already present.
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
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        // HttpClient must outlive the response stream — do not dispose before the copy finishes.
        using HttpClient httpClient = new(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage response = await SendWithRangeAsync(httpClient, url, existingSize, cancellationToken).ConfigureAwait(false);

        if (_logger.IsInfo)
            _logger.Info($"Server response: {response.StatusCode}, ETag: {response.Headers.ETag}, Last-Modified: {response.Content.Headers.LastModified}");

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (_logger.IsInfo)
                _logger.Info("Snapshot file already fully downloaded (server returned 416).");
            return;
        }

        (FileMode fileMode, long bytesToSkip, long? totalSize) = response.StatusCode switch
        {
            // Server honored the Range request — append the remaining bytes.
            HttpStatusCode.PartialContent =>
                (FileMode.Append, 0L, existingSize + response.Content.Headers.ContentLength),

            // Server returned the full file, but we already have part of it —
            // skip the already-downloaded portion in the stream and append the rest.
            HttpStatusCode.OK when existingSize > 0 =>
                (FileMode.Append, existingSize, response.Content.Headers.ContentLength),

            HttpStatusCode.OK =>
                (FileMode.Create, 0L, response.Content.Headers.ContentLength),

            _ => throw new IOException($"Unexpected HTTP status: {response.StatusCode}")
        };

        if (bytesToSkip > 0 && _logger.IsWarn)
            _logger.Warn($"Server does not support range requests. Consuming {bytesToSkip} already-downloaded bytes to resume.");

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream fileStream = new(destinationPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        using ProgressTracker progressTracker = new(logManager, timerFactory, TimeSpan.FromSeconds(5), existingSize, totalSize);

        if (bytesToSkip > 0)
            await SkipBytesAsync(contentStream, bytesToSkip, cancellationToken).ConfigureAwait(false);

        await CopyWithProgressAsync(contentStream, fileStream, progressTracker, cancellationToken).ConfigureAwait(false);

        if (_logger.IsInfo)
            _logger.Info($"Snapshot downloaded to {destinationPath}.");
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

    private static async Task SkipBytesAsync(Stream stream, long bytesToSkip, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long remaining = bytesToSkip;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException($"Stream ended prematurely while skipping: {remaining} of {bytesToSkip} bytes remaining.");
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
