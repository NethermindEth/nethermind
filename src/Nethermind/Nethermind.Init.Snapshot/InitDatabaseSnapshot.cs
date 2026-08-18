// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Abstractions;
using System.Security.Cryptography;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.Extensions;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

/// <summary>
/// Optionally bootstraps the database from a remote snapshot before the node starts.
/// The download is resumable and idempotent: a checkpoint file tracks progress so that
/// restarts skip already-completed stages.
/// </summary>
[RunnerStepDependencies(
    dependencies: [],
    dependents: [typeof(InitializeBlockTree), typeof(DatabaseMigrations), typeof(StartLogIndex)])]
public class InitDatabaseSnapshot(
    INethermindApi api,
    [KeyFilter(nameof(IInitConfig.BaseDbPath))] IDriveInfo[] drives) : IStep
{
    private const double ExtractionSpaceMultiplier = 1.5;
    private const int MaxStreamingConnections = 16;
    private const int ExtractionRestartDelaySeconds = 5;
    private const int InitialRetryDelaySeconds = 5;
    private const int MaxRetryDelaySeconds = 300;
    private const int ChecksumBufferSize = 65536;
    private const int ChecksumProgressIntervalSeconds = 30;

    private readonly ILogger _logger = api.LogManager.GetClassLogger<InitDatabaseSnapshot>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (!IsInMemoryOrReadOnlyMode())
            await InitDbFromSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsInMemoryOrReadOnlyMode() =>
        api.Config<IInitConfig>().DiagnosticMode is
            DiagnosticMode.RpcDb or
            DiagnosticMode.ReadOnlyDb or
            DiagnosticMode.MemDb;

    private async Task InitDbFromSnapshotAsync(CancellationToken cancellationToken)
    {
        ISnapshotConfig snapshotConfig = api.Config<ISnapshotConfig>();
        string dbPath = api.Config<IInitConfig>().BaseDbPath;
        string snapshotUrl = snapshotConfig.DownloadUrl
            ?? throw new InvalidOperationException("Snapshot download URL is not configured.");
        string snapshotPath = Path.Combine(snapshotConfig.SnapshotDirectory, snapshotConfig.SnapshotFileName);

        if (snapshotConfig.StripComponents < 0)
            throw new InvalidOperationException($"Snapshot.StripComponents must be non-negative, got {snapshotConfig.StripComponents}.");

        if (snapshotConfig.Streaming && snapshotConfig.StreamingConnections is < 1 or > MaxStreamingConnections)
            throw new InvalidOperationException($"Snapshot.StreamingConnections must be between 1 and {MaxStreamingConnections}, got {snapshotConfig.StreamingConnections}.");

        SnapshotCheckpoint checkpoint = new(snapshotConfig, api.LogManager);

        if (DatabaseExists(dbPath))
        {
            if (checkpoint.Read() < SnapshotStage.Extracted)
            {
                if (_logger.IsInfo)
                    _logger.Info("Extraction did not complete last time. Restarting. To interrupt press Ctrl^C");
                await Task.Delay(TimeSpan.FromSeconds(ExtractionRestartDelaySeconds), cancellationToken).ConfigureAwait(false);
                Directory.Delete(dbPath, true);
            }
            else
            {
                if (_logger.IsInfo)
                    _logger.Info($"Database already exists at {dbPath}. Skipping snapshot initialization.");
                return;
            }
        }
        else if (checkpoint.Read() >= SnapshotStage.Extracted)
        {
            if (_logger.IsWarn)
                _logger.Warn($"The snapshot checkpoint indicates a completed extraction, but the database at {dbPath} is missing or empty. Reinitializing from the snapshot.");
            checkpoint.Advance(SnapshotStage.Started);
        }

        Directory.CreateDirectory(snapshotConfig.SnapshotDirectory);

        if (snapshotConfig.Streaming)
        {
            if (checkpoint.Read() < SnapshotStage.Downloaded || !File.Exists(snapshotPath))
            {
                StreamingSnapshotInitializer initializer = new(
                    snapshotConfig, snapshotUrl, dbPath, drives,
                    SnapshotStreamSettings.Default(snapshotConfig.StreamingConnections), api.LogManager);
                await initializer.InitializeAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_logger.IsWarn)
                _logger.Warn($"A fully downloaded snapshot archive exists at {snapshotPath}. Extracting it instead of streaming.");
        }

        using SnapshotDownloader downloader = new(api.LogManager);
        await DownloadWithRetryAsync(downloader, snapshotUrl, snapshotPath, checkpoint, cancellationToken).ConfigureAwait(false);

        bool checksumPassed = await VerifyChecksumAsync(snapshotPath, snapshotConfig, checkpoint, cancellationToken).ConfigureAwait(false);
        if (!checksumPassed)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Deleting invalid snapshot file '{snapshotPath}' and resetting checkpoint for re-download on next run.");
            File.Delete(snapshotPath);
            checkpoint.Advance(SnapshotStage.Started);
            return;
        }

        await ExtractAsync(snapshotPath, dbPath, snapshotConfig.StripComponents, checkpoint, cancellationToken).ConfigureAwait(false);

        if (_logger.IsInfo)
        {
            _logger.Info("Database successfully initialized from snapshot.");
            _logger.Info($"Deleting snapshot file {snapshotPath}.");
        }

        File.Delete(snapshotPath);
        checkpoint.Advance(SnapshotStage.Completed);
    }

    private async Task DownloadWithRetryAsync(
        SnapshotDownloader downloader, string url, string destinationPath, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.Read() >= SnapshotStage.Downloaded)
            return;

        await CheckDiskSpaceBeforeDownloadAsync(downloader, url, destinationPath, cancellationToken).ConfigureAwait(false);

        TimeSpan retryDelay = TimeSpan.FromSeconds(InitialRetryDelaySeconds);
        long lastSize = GetFileSize(destinationPath);

        while (true)
        {
            try
            {
                await downloader.DownloadAsync(url, destinationPath, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (HttpRequestException e) when (SnapshotHttpClient.IsPermanentHttpError(e))
            {
                if (_logger.IsError)
                    _logger.Error($"Snapshot download failed with permanent HTTP error {(int?)e.StatusCode}. Aborting.");
                throw;
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                long currentSize = GetFileSize(destinationPath);
                if (currentSize > lastSize)
                    retryDelay = TimeSpan.FromSeconds(InitialRetryDelaySeconds);
                lastSize = currentSize;

                if (_logger.IsError)
                    _logger.Error($"Snapshot download failed. Retrying in {retryDelay.TotalSeconds}s. Error: {e}");
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaxRetryDelaySeconds));
            }
        }

        checkpoint.Advance(SnapshotStage.Downloaded);
    }

    private static bool DatabaseExists(string dbPath)
    {
        if (File.Exists(dbPath))
            return true;
        if (!Directory.Exists(dbPath))
            return false;

        foreach (string entry in Directory.EnumerateFileSystemEntries(dbPath))
        {
            if (Path.GetFileName(entry) != "lost+found")
                return true;
        }

        return false;
    }

    private long GetFileSize(string path)
    {
        IFileInfo file = api.FileSystem.FileInfo.New(path);
        return file.Exists ? file.Length : 0;
    }

    private async Task<bool> VerifyChecksumAsync(
        string snapshotPath, ISnapshotConfig config, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.Read() >= SnapshotStage.Verified)
            return true;

        if (config.Checksum is not null)
        {
            if (_logger.IsInfo)
                _logger.Info($"Verifying snapshot checksum {config.Checksum}.");

            byte[] actual = await ComputeChecksumAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            if (!VerifyChecksum(actual, config.Checksum, "Aborting snapshot initialization, but the node will continue running.", _logger))
                return false;
        }
        else if (_logger.IsWarn)
        {
            _logger.Warn("Snapshot checksum is not configured.");
        }

        checkpoint.Advance(SnapshotStage.Verified);
        return true;
    }

    internal static bool VerifyChecksum(byte[] actual, string? expectedHex, string onMismatch, ILogger logger)
    {
        if (expectedHex is null)
        {
            if (logger.IsWarn)
                logger.Warn("Snapshot checksum is not configured.");
            return true;
        }

        byte[] expected = Bytes.FromHexString(expectedHex);
        if (Bytes.AreEqual(actual, expected))
        {
            if (logger.IsInfo)
                logger.Info("Snapshot checksum verified.");
            return true;
        }

        if (logger.IsError)
            logger.Error(
                $"Snapshot checksum verification failed. Expected: {expectedHex}, actual: {Convert.ToHexString(actual).ToLowerInvariant()}. {onMismatch}");
        return false;
    }

    private async Task ExtractAsync(
        string snapshotPath, string dbPath, int stripComponents, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.Read() >= SnapshotStage.Extracted)
            return;

        CheckDiskSpace(drives, GetRequiredSpaceForExtraction(GetFileSize(snapshotPath)), "extract");

        SnapshotExtractor extractor = new(api.LogManager);
        await extractor.ExtractAsync(snapshotPath, dbPath, stripComponents, cancellationToken).ConfigureAwait(false);
        checkpoint.Advance(SnapshotStage.Extracted);
    }

    private async Task CheckDiskSpaceBeforeDownloadAsync(
        SnapshotDownloader downloader, string url, string destinationPath, CancellationToken cancellationToken)
    {
        long existingSize = GetFileSize(destinationPath);
        long? totalSize;
        try
        {
            totalSize = await downloader.GetTotalSizeAsync(url, existingSize, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Could not determine the snapshot size upfront. Skipping the pre-download disk space check. Error: {e.Message}");
            return;
        }

        if (totalSize is null)
        {
            if (_logger.IsWarn)
                _logger.Warn("The server did not report the snapshot size. Skipping the pre-download disk space check.");
            return;
        }

        CheckDiskSpace(drives, GetRequiredSpaceForDownload(totalSize.Value, existingSize), "download and extract");
    }

    internal static long GetRequiredSpaceForDownload(long totalSize, long existingSize) =>
        totalSize - existingSize + GetRequiredSpaceForExtraction(totalSize);

    internal static long GetRequiredSpaceForExtraction(long snapshotSize) =>
        (long)(snapshotSize * ExtractionSpaceMultiplier);

    internal static void CheckDiskSpace(IDriveInfo[] drives, long required, string operation)
    {
        foreach (IDriveInfo drive in drives)
        {
            if (drive.AvailableFreeSpace < required)
                throw new IOException(
                    $"Insufficient disk space on '{drive.RootDirectory.FullName}' to {operation} the snapshot: " +
                    $"need at least {required} bytes, {drive.AvailableFreeSpace} available.");
        }
    }

    private async Task<byte[]> ComputeChecksumAsync(string filePath, CancellationToken cancellationToken)
    {
        long fileSize = new FileInfo(filePath).Length;
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChecksumBufferSize);
        try
        {
            await using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read,
                FileShare.None, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long bytesHashed = 0;
            DateTime nextLog = DateTime.UtcNow.AddSeconds(ChecksumProgressIntervalSeconds);

            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, bytesRead);
                bytesHashed += bytesRead;

                if (_logger.IsInfo && fileSize > 0 && DateTime.UtcNow >= nextLog)
                {
                    _logger.Info($"Snapshot checksum progress: {bytesHashed * 100 / fileSize}%");
                    nextLog = DateTime.UtcNow.AddSeconds(ChecksumProgressIntervalSeconds);
                }
            }

            return hasher.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
