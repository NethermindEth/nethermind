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

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await StreamAndExtractAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SnapshotSourceChangedException e) when (attempt < MaxSourceChangedRestarts)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"{e.Message} Restarting the snapshot download.");
                DeleteDatabase();
            }
        }
    }

    private async Task StreamAndExtractAsync(SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        using SnapshotHttpClient client = new();
        SnapshotRemoteInfo remoteInfo = await client.ProbeAsync(url, cancellationToken).ConfigureAwait(false);
        LogMode(remoteInfo);
        CheckDiskSpace(remoteInfo.Length);

        await using SnapshotHttpStream stream = new(client, url, remoteInfo, settings, logManager, cancellationToken);
        SnapshotExtractor extractor = new(logManager);
        string extension = Path.GetExtension(config.SnapshotFileName).ToLowerInvariant();
        byte[] checksum;
        try
        {
            await extractor.ExtractTarStreamAsync(stream, dbPath, extension, config.StripComponents, cancellationToken).ConfigureAwait(false);
            checksum = await stream.FinishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or ZstdException
                                  && e is not SnapshotSourceChangedException)
        {
            if (_logger.IsError)
                _logger.Error($"Snapshot streaming failed: {e.Message} Deleting the partially extracted database; the node will continue running.");
            DeleteDatabase();
            return;
        }

        if (!VerifyChecksum(checksum))
        {
            DeleteDatabase();
            return;
        }

        checkpoint.Advance(SnapshotStage.Completed);
        if (_logger.IsInfo)
            _logger.Info("Database successfully initialized from streamed snapshot.");
    }

    private static void EnsureStreamableArchive(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".tar" or ".zst" or ".zstd" or ".gz"))
            throw new NotSupportedException(
                $"Snapshot streaming supports only tar-based archives (.tar, .tar.zst, .tar.gz); got '{fileName}'. Disable Snapshot.Streaming to use other formats.");
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

        long required = InitDatabaseSnapshot.GetRequiredSpaceForExtraction(snapshotLength.Value);
        InitDatabaseSnapshot.CheckDiskSpace(drives, required, "extract");
    }

    private bool VerifyChecksum(byte[] actual) =>
        InitDatabaseSnapshot.VerifyChecksum(
            actual, config.Checksum, "Deleting the extracted database; the node will continue running.", _logger);

    private void DeleteDatabase()
    {
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);
    }
}
