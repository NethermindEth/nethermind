// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

/// <summary>
/// Downloads a snapshot file from a URL with resumable download support.
/// </summary>
internal sealed class SnapshotDownloader(ILogManager logManager) : IDisposable
{
    private const int BufferSize = 65536;
    private const int ResumeWarningDelaySeconds = 5;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallTimeout = SnapshotHttpClient.DefaultStallTimeout;

    // A single client is shared for all retries to preserve the connection pool.
    private readonly SnapshotHttpClient _client = new();
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

        using HttpResponseMessage response = await _client.GetAsync(
            url, existingSize > 0 ? new RangeHeaderValue(existingSize, null) : null, ifRange: null, cancellationToken).ConfigureAwait(false);

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

        ulong initialProgress = fileMode == FileMode.Append ? (ulong)existingSize : 0UL;
        using ProgressReporter progress = new("Snapshot download", logManager, (ulong)(totalSize ?? 0), ProgressInterval);
        progress.Logger.SetFormat(SnapshotProgress.FormatBytes("Snapshot download", totalSize));
        progress.Update(initialProgress);

        if (bytesToSkip > 0)
            await SnapshotHttpClient.SkipAsync(contentStream, bytesToSkip, StallTimeout, cancellationToken).ConfigureAwait(false);

        await CopyWithProgressAsync(contentStream, fileStream, progress, cancellationToken).ConfigureAwait(false);

        if (_logger.IsInfo)
            _logger.Info($"Snapshot downloaded to {destinationPath}.");
    }

    public async Task<long?> GetTotalSizeAsync(string url, long existingSize, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            url, existingSize > 0 ? new RangeHeaderValue(existingSize, null) : null, ifRange: null, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return existingSize;

        return ResolveCopyStrategy(response.StatusCode, existingSize, response.Content.Headers.ContentLength).totalSize;
    }

    public void Dispose() => _client.Dispose();

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

    private static async Task CopyWithProgressAsync(
        Stream source, FileStream destination, ProgressReporter progress, CancellationToken cancellationToken)
    {
        using StallGuardedReader reader = new(StallTimeout, cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            ulong downloaded = progress.Logger.CurrentValue;
            int bytesRead;
            while ((bytesRead = await reader.ReadAsync(source, buffer.AsMemory(0, BufferSize)).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                downloaded += (ulong)bytesRead;
                progress.Update(downloaded);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

}
