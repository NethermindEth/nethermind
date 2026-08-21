// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Logging;
using ZstdSharp;

namespace Nethermind.Init.Snapshot;

internal sealed class StreamingSnapshotInitializer(
    ISnapshotConfig config,
    string url,
    string dbPath,
    IDriveInfo[] drives,
    SnapshotStreamSettings settings,
    ILogManager logManager)
{
    private const int MaxSourceChangedRestarts = 3;

    private readonly ILogger _logger = logManager.GetClassLogger<StreamingSnapshotInitializer>();

    public async Task InitializeAsync(SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        EnsureStreamableArchive(config.SnapshotFileName);
        DeleteStaleArchive();
        using SnapshotHttpClient client = new();

        for (int attempt = 1; attempt <= MaxSourceChangedRestarts; attempt++)
        {
            SnapshotRemoteInfo remoteInfo = await ProbeWithRetryAsync(client, cancellationToken).ConfigureAwait(false);
            if (remoteInfo.Length is null && config.Checksum is null)
                throw new InvalidOperationException(
                    "The server does not report the snapshot size and Snapshot.Checksum is not set, so a truncated download could not be detected. Set Snapshot.Checksum or disable Snapshot.Streaming.");

            LogMode(remoteInfo);
            CheckDiskSpace(remoteInfo.Length);

            bool verified;
            try
            {
                verified = await StreamAndExtractAsync(client, remoteInfo, checkpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (SnapshotSourceChangedException e)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"{e.Message} Restarting the snapshot download.");
                DeleteDatabase();
                continue;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or ZstdException)
            {
                if (_logger.IsError)
                    _logger.Error($"Snapshot streaming failed: {e.Message} Deleting the partially extracted database.");
                DeleteDatabase();
                LogContinuingWithoutSnapshot();
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_logger.IsError)
                    _logger.Error("Snapshot streaming failed. Deleting the partially extracted database.", e);
                DeleteDatabase();
                throw;
            }

            if (!verified)
            {
                DeleteDatabase();
                LogContinuingWithoutSnapshot();
            }

            return;
        }

        if (_logger.IsError)
            _logger.Error($"The snapshot kept changing on the server across {MaxSourceChangedRestarts} attempts. Giving up; the node will continue running.");
    }

    private async Task<bool> StreamAndExtractAsync(
        SnapshotHttpClient client, SnapshotRemoteInfo remoteInfo, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        string? expectedChecksum = config.Checksum;
        byte[]? checksum;
        await using (SnapshotHttpStream stream = new(
            client, url, remoteInfo, settings with { ComputeChecksum = expectedChecksum is not null }, logManager, cancellationToken))
        {
            SnapshotExtractor extractor = new(logManager);
            string extension = Path.GetExtension(config.SnapshotFileName).ToLowerInvariant();
            await extractor.ExtractTarStreamAsync(stream, dbPath, extension, config.StripComponents, cancellationToken).ConfigureAwait(false);
            checksum = await stream.FinishAsync(cancellationToken).ConfigureAwait(false);
        }

        if (expectedChecksum is null)
        {
            if (_logger.IsWarn)
                _logger.Warn("Snapshot checksum is not configured.");
        }
        else if (checksum is null
                 || !SnapshotChecksum.Verify(checksum, expectedChecksum, "Deleting the extracted database.", _logger))
        {
            return false;
        }

        checkpoint.Advance(SnapshotStage.Completed);
        if (_logger.IsInfo)
            _logger.Info("Database successfully initialized from streamed snapshot.");
        return true;
    }

    private async Task<SnapshotRemoteInfo> ProbeWithRetryAsync(SnapshotHttpClient client, CancellationToken cancellationToken)
    {
        TimeSpan retryDelay = settings.InitialRetryDelay;
        while (true)
        {
            try
            {
                return await client.ProbeAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (SnapshotHttpClient.IsPermanentHttpError(e))
            {
                throw;
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Snapshot probe failed. Retrying in {retryDelay.TotalSeconds}s. Error: {e.Message}");
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = retryDelay * 2 > settings.MaxRetryDelay ? settings.MaxRetryDelay : retryDelay * 2;
            }
        }
    }

    private static void EnsureStreamableArchive(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!SnapshotArchiveFormat.IsStreamable(extension))
            throw new NotSupportedException(
                $"Snapshot streaming supports only tar-based archives (.tar, .tar.zst, .tar.gz), but Snapshot.SnapshotFileName is '{fileName}'. Set Snapshot.SnapshotFileName to match the archive or disable Snapshot.Streaming.");
    }

    private void DeleteStaleArchive()
    {
        string archivePath = Path.Combine(config.SnapshotDirectory, config.SnapshotFileName);
        if (!File.Exists(archivePath))
            return;

        if (_logger.IsWarn)
            _logger.Warn($"Deleting snapshot file {archivePath}; the streaming download does not use it.");
        File.Delete(archivePath);
    }

    private void LogMode(SnapshotRemoteInfo remoteInfo)
    {
        if (!remoteInfo.SupportsRanges)
        {
            if (_logger.IsWarn)
                _logger.Warn("Snapshot server does not support range requests. Streaming with a single connection; every resumed connection re-reads the file from the beginning.");
        }
        else if (_logger.IsInfo)
        {
            _logger.Info($"Streaming snapshot from {url} with {settings.Connections} connections.");
        }
    }

    private void CheckDiskSpace(long? snapshotLength)
    {
        if (snapshotLength is null)
        {
            if (_logger.IsWarn)
                _logger.Warn("Snapshot size is unknown. Skipping the disk space check.");
            return;
        }

        SnapshotDiskSpace.Check(drives, SnapshotDiskSpace.GetRequiredSpaceForExtraction(snapshotLength.Value), "extract");
    }

    private void LogContinuingWithoutSnapshot()
    {
        if (_logger.IsInfo)
            _logger.Info("The node will continue running without a snapshot.");
    }

    private void DeleteDatabase()
    {
        try
        {
            SnapshotDatabase.Delete(dbPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Could not clean up the database at {dbPath}, so the node must not start on top of it. Delete it manually before restarting.", e);
        }
    }
}
