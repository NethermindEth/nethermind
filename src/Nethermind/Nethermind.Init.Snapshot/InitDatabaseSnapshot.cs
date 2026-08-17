// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Abstractions;
using System.Net;
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

        SnapshotCheckpoint checkpoint = new(snapshotConfig, api.LogManager);

        if (Path.Exists(dbPath))
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

        Directory.CreateDirectory(snapshotConfig.SnapshotDirectory);

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
            catch (HttpRequestException e) when (
                e.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
                    and not HttpStatusCode.TooManyRequests
                    and not HttpStatusCode.RequestedRangeNotSatisfiable)
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

        if (config.Checksum is null)
        {
            if (_logger.IsWarn)
                _logger.Warn("Snapshot checksum is not configured.");
        }
        else
        {
            if (_logger.IsInfo)
                _logger.Info($"Verifying snapshot checksum {config.Checksum}.");

            byte[] expected = Bytes.FromHexString(config.Checksum);
            byte[] actual = await ComputeChecksumAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

            if (!Bytes.AreEqual(actual, expected))
            {
                if (_logger.IsError)
                    _logger.Error($"Snapshot checksum verification failed. Expected: {config.Checksum}, actual: {Convert.ToHexString(actual).ToLowerInvariant()}. Aborting snapshot initialization, but the node will continue running.");
                return false;
            }

            if (_logger.IsInfo)
                _logger.Info("Snapshot checksum verified.");
        }

        checkpoint.Advance(SnapshotStage.Verified);
        return true;
    }

    private async Task ExtractAsync(
        string snapshotPath, string dbPath, int stripComponents, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.Read() >= SnapshotStage.Extracted)
            return;

        CheckDiskSpace(GetRequiredSpaceForExtraction(GetFileSize(snapshotPath)), "extract");

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
        catch (Exception e) when (e is IOException or HttpRequestException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
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

        CheckDiskSpaceBeforeDownload(totalSize.Value, existingSize, Path.GetDirectoryName(destinationPath)!);
    }

    private void CheckDiskSpaceBeforeDownload(long totalSize, long existingSize, string snapshotDirectory)
    {
        IDriveInfo[] snapshotDrives = api.FileSystem.GetDriveInfos(snapshotDirectory);
        if (snapshotDrives.Length == 0)
        {
            CheckDiskSpace(GetRequiredSpaceForDownload(totalSize, existingSize), "download and extract");
            return;
        }

        long remainingDownload = totalSize - existingSize;
        long extraction = GetRequiredSpaceForExtraction(totalSize);

        foreach (IDriveInfo drive in drives)
        {
            bool holdsArchive = snapshotDrives.Any(snapshotDrive => IsSameDrive(snapshotDrive, drive));
            CheckDiskSpace(drive, holdsArchive ? remainingDownload + extraction : extraction, "download and extract");
        }

        foreach (IDriveInfo snapshotDrive in snapshotDrives)
        {
            if (!drives.Any(drive => IsSameDrive(drive, snapshotDrive)))
                CheckDiskSpace(snapshotDrive, remainingDownload, "download");
        }
    }

    private static bool IsSameDrive(IDriveInfo first, IDriveInfo second) =>
        string.Equals(first.RootDirectory.FullName, second.RootDirectory.FullName, StringComparison.Ordinal);

    internal static long GetRequiredSpaceForDownload(long totalSize, long existingSize) =>
        totalSize - existingSize + GetRequiredSpaceForExtraction(totalSize);

    internal static long GetRequiredSpaceForExtraction(long snapshotSize) =>
        (long)(snapshotSize * ExtractionSpaceMultiplier);

    private void CheckDiskSpace(long required, string operation)
    {
        foreach (IDriveInfo drive in drives)
            CheckDiskSpace(drive, required, operation);
    }

    private static void CheckDiskSpace(IDriveInfo drive, long required, string operation)
    {
        if (drive.AvailableFreeSpace < required)
            throw new IOException(
                $"Insufficient disk space on '{drive.RootDirectory.FullName}' to {operation} the snapshot: " +
                $"need at least {required} bytes, {drive.AvailableFreeSpace} available.");
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
