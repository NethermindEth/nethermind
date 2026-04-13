// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using Nethermind.Api;
using Nethermind.Core.Extensions;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

/// <summary>
/// Extends <see cref="InitDatabase"/> to optionally bootstrap the database from a
/// remote snapshot before the node starts. The download is resumable and idempotent:
/// a checkpoint file tracks progress so that restarts skip already-completed stages.
/// </summary>
public class InitDatabaseSnapshot(INethermindApi api) : InitDatabase
{
    private const int ExtractionRestartDelaySeconds = 5;
    private const int InitialRetryDelaySeconds = 5;
    private const int MaxRetryDelaySeconds = 300;
    private const int ChecksumBufferSize = 65536;
    private const int ChecksumProgressIntervalSeconds = 30;

    private readonly ILogger _logger = api.LogManager.GetClassLogger<InitDatabaseSnapshot>();

    public override async Task Execute(CancellationToken cancellationToken)
    {
        if (!IsInMemoryOrReadOnlyMode())
            await InitDbFromSnapshotAsync(cancellationToken).ConfigureAwait(false);

        await base.Execute(cancellationToken).ConfigureAwait(false);
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

        using SnapshotDownloader downloader = new(api.LogManager, api.TimerFactory);
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

        TimeSpan retryDelay = TimeSpan.FromSeconds(InitialRetryDelaySeconds);

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
                if (_logger.IsError)
                    _logger.Error($"Snapshot download failed. Retrying in {retryDelay.TotalSeconds}s. Error: {e}");
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaxRetryDelaySeconds));
            }
        }

        checkpoint.Advance(SnapshotStage.Downloaded);
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

        CheckDiskSpace(dbPath, snapshotPath);

        SnapshotExtractor extractor = new(api.LogManager);
        await extractor.ExtractAsync(snapshotPath, dbPath, stripComponents, cancellationToken).ConfigureAwait(false);
        checkpoint.Advance(SnapshotStage.Extracted);
    }

    private static void CheckDiskSpace(string dbPath, string snapshotPath)
    {
        long snapshotSize = new FileInfo(snapshotPath).Length;
        string root = Path.GetPathRoot(dbPath) ?? "/";
        DriveInfo drive = new(root);

        // May still underestimate for highly compressed archives.
        if (drive.AvailableFreeSpace < snapshotSize * 2)
            throw new IOException($"Insufficient disk space to extract snapshot: need at least {snapshotSize * 2} bytes, {drive.AvailableFreeSpace} available on '{root}'.");
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
