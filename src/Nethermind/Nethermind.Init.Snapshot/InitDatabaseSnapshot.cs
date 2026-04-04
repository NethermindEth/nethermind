// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
public class InitDatabaseSnapshot : InitDatabase
{
    private readonly INethermindApi _api;
    private readonly ILogger _logger;

    public InitDatabaseSnapshot(INethermindApi api)
    {
        _api = api;
        _logger = _api.LogManager.GetClassLogger<InitDatabaseSnapshot>();
    }

    public override async Task Execute(CancellationToken cancellationToken)
    {
        if (!IsInMemoryOrReadOnlyMode())
            await InitDbFromSnapshotAsync(cancellationToken).ConfigureAwait(false);

        await base.Execute(cancellationToken).ConfigureAwait(false);
    }

    private bool IsInMemoryOrReadOnlyMode() =>
        _api.Config<IInitConfig>().DiagnosticMode is
            DiagnosticMode.RpcDb or
            DiagnosticMode.ReadOnlyDb or
            DiagnosticMode.MemDb;

    private async Task InitDbFromSnapshotAsync(CancellationToken cancellationToken)
    {
        ISnapshotConfig snapshotConfig = _api.Config<ISnapshotConfig>();
        string dbPath = _api.Config<IInitConfig>().BaseDbPath;
        string snapshotUrl = snapshotConfig.DownloadUrl
            ?? throw new InvalidOperationException("Snapshot download URL is not configured.");
        string snapshotPath = Path.Combine(snapshotConfig.SnapshotDirectory, snapshotConfig.SnapshotFileName);

        SnapshotCheckpoint checkpoint = new(snapshotConfig);

        if (Path.Exists(dbPath))
        {
            if (checkpoint.Read() < SnapshotStage.Extracted)
            {
                if (_logger.IsInfo)
                    _logger.Info("Extraction did not complete last time. Restarting. To interrupt press Ctrl^C");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
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

        await DownloadWithRetryAsync(snapshotUrl, snapshotPath, checkpoint, cancellationToken).ConfigureAwait(false);

        bool checksumPassed = await VerifyChecksumAsync(snapshotPath, snapshotConfig, checkpoint, cancellationToken).ConfigureAwait(false);
        if (!checksumPassed)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Deleting invalid snapshot file '{snapshotPath}' and resetting checkpoint for re-download on next run.");
            if (File.Exists(snapshotPath))
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
        string url, string destinationPath, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.Read() >= SnapshotStage.Downloaded)
            return;

        SnapshotDownloader downloader = new(_api.LogManager, _api.TimerFactory);

        while (true)
        {
            try
            {
                await downloader.DownloadAsync(url, destinationPath, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (HttpRequestException e) when (
                e.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
                    and not HttpStatusCode.TooManyRequests)
            {
                if (_logger.IsError)
                    _logger.Error($"Snapshot download failed with permanent HTTP error {(int?)e.StatusCode}. Aborting.");
                throw;
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                if (_logger.IsError)
                    _logger.Error($"Snapshot download failed. Retrying in 5 seconds. Error: {e}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
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

            bool valid = await ComputeAndCompareChecksumAsync(snapshotPath, config.Checksum, cancellationToken).ConfigureAwait(false);
            if (!valid)
            {
                if (_logger.IsError)
                    _logger.Error("Snapshot checksum verification failed. Aborting snapshot initialization, but the node will continue running.");
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

        SnapshotExtractor extractor = new(_api.LogManager);
        await extractor.ExtractAsync(snapshotPath, dbPath, stripComponents, cancellationToken).ConfigureAwait(false);
        checkpoint.Advance(SnapshotStage.Extracted);
    }

    private static async Task<bool> ComputeAndCompareChecksumAsync(
        string filePath, string expectedChecksum, CancellationToken cancellationToken)
    {
        byte[] expected = Bytes.FromHexString(expectedChecksum);
        await using FileStream fileStream = File.OpenRead(filePath);
        byte[] actual = await SHA256.HashDataAsync(fileStream, cancellationToken).ConfigureAwait(false);
        return Bytes.AreEqual(actual, expected);
    }
}
