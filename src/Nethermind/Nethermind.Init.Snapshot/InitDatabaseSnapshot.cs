// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Nethermind.Core.Extensions;

namespace Nethermind.Init.Snapshot;

public class InitDatabaseSnapshot : InitDatabase
{
    private const int BufferSize = 8192;

    private readonly INethermindApi _api;
    private readonly ILogger _logger;

    public InitDatabaseSnapshot(INethermindApi api) : base()
    {
        _api = api;
        _logger = _api.LogManager.GetClassLogger();
    }

    public override async Task Execute(CancellationToken cancellationToken)
    {
        switch (_api.Config<IInitConfig>().DiagnosticMode)
        {
            case DiagnosticMode.RpcDb:
            case DiagnosticMode.ReadOnlyDb:
            case DiagnosticMode.MemDb:
                break;
            default:
                await InitDbFromSnapshot(cancellationToken);
                break;
        }

        await base.Execute(cancellationToken);
    }

    private async Task InitDbFromSnapshot(CancellationToken cancellationToken)
    {

        ISnapshotConfig snapshotConfig = _api.Config<ISnapshotConfig>();
        string dbPath = _api.Config<IInitConfig>().BaseDbPath;
        string snapshotUrl = snapshotConfig.DownloadUrl ??
                             throw new InvalidOperationException("Snapshot download URL is not configured");
        string snapshotFileName = Path.Combine(snapshotConfig.SnapshotDirectory, snapshotConfig.SnapshotFileName);

        if (Path.Exists(dbPath))
        {
            if (GetCheckpoint(snapshotConfig) < Stage.Extracted)
            {
                if (_logger.IsInfo)
                    _logger.Info($"Extracting wasn't finished last time, restarting it. To interrupt press Ctrl^C");
                // Wait few seconds if user wants to stop reinitialization
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                Directory.Delete(dbPath, true);
            }
            else
            {
                if (_logger.IsInfo)
                    _logger.Info($"Database already exists at {dbPath}. Interrupting");

                return;
            }
        }

        Directory.CreateDirectory(snapshotConfig.SnapshotDirectory);

        if (GetCheckpoint(snapshotConfig) < Stage.Downloaded)
        {
            while (true)
            {
                try
                {
                    await DownloadSnapshotTo(snapshotUrl, snapshotFileName, cancellationToken);
                    break;
                }
                catch (IOException e)
                {
                    if (_logger.IsError)
                        _logger.Error($"Snapshot download failed. Retrying in 5 seconds. Error: {e}");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            SetCheckpoint(snapshotConfig, Stage.Downloaded);
        }

        if (GetCheckpoint(snapshotConfig) < Stage.Verified)
        {
            if (snapshotConfig.Checksum is not null)
            {
                bool isChecksumValid = await VerifyChecksum(snapshotFileName, snapshotConfig.Checksum, cancellationToken);
                if (!isChecksumValid)
                {
                    if (_logger.IsError)
                        _logger.Error("Snapshot checksum verification failed. Aborting, but will continue running.");
                    return;
                }

                if (_logger.IsInfo)
                    _logger.Info("Snapshot checksum verified.");
            }
            else if (_logger.IsWarn)
                _logger.Warn("Snapshot checksum is not configured");
            SetCheckpoint(snapshotConfig, Stage.Verified);
        }

        await ExtractSnapshotTo(snapshotFileName, dbPath, cancellationToken);
        SetCheckpoint(snapshotConfig, Stage.Extracted);

        if (_logger.IsInfo)
        {
            _logger.Info("Database successfully initialized from snapshot.");
            _logger.Info($"Deleting snapshot file {snapshotFileName}.");
        }

        File.Delete(snapshotFileName);

        SetCheckpoint(snapshotConfig, Stage.End);
    }

    private async Task DownloadSnapshotTo(
        string snapshotUrl, string snapshotFileName, CancellationToken cancellationToken)
    {
        FileInfo snapshotFile = new(snapshotFileName);
        long existingSize = snapshotFile.Exists ? snapshotFile.Length : 0;

        if (_logger.IsInfo)
            _logger.Info($"Downloading snapshot from {snapshotUrl} to {snapshotFile.FullName}");

        if (existingSize > 0)
        {
            if (_logger.IsWarn)
                _logger.Warn("Snapshot file already exists. Resuming download. To interrupt press Ctrl^C");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        using HttpClient httpClient = new();
        HttpRequestMessage request = new(HttpMethod.Get, snapshotUrl);
        if (existingSize > 0)
            request.Headers.Range = new RangeHeaderValue(existingSize, null);

        using HttpResponseMessage response =
            (await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            .EnsureSuccessStatusCode();

        (FileMode fileMode, long bytesToSkip, long? totalSize) = response.StatusCode switch
        {
            // Server honoured our Range request — append remaining bytes.
            HttpStatusCode.PartialContent =>
                (FileMode.Append, 0L, existingSize + response.Content.Headers.ContentLength),

            // Server returned the full file. If we had a partial file, skip the already-downloaded
            // portion in the stream and append; otherwise start fresh.
            HttpStatusCode.OK when existingSize > 0 =>
                (FileMode.Append, existingSize, response.Content.Headers.ContentLength),

            HttpStatusCode.OK =>
                (FileMode.Create, 0L, response.Content.Headers.ContentLength),

            _ => throw new IOException($"Unexpected HTTP status: {response.StatusCode}")
        };

        if (bytesToSkip > 0 && _logger.IsWarn)
            _logger.Warn($"Server does not support range requests. Consuming {bytesToSkip} already-downloaded bytes to resume.");

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream fileStream = new(snapshotFileName, fileMode, FileAccess.Write, FileShare.None, BufferSize, true);

        using ProgressTracker progressTracker = new(
            _api.LogManager, _api.TimerFactory, TimeSpan.FromSeconds(5), existingSize, totalSize);

        if (bytesToSkip > 0)
            await SkipAsync(contentStream, bytesToSkip, cancellationToken);

        await CopyAsync(contentStream, fileStream, progressTracker, cancellationToken);

        if (_logger.IsInfo)
            _logger.Info($"Snapshot downloaded to {snapshotFileName}.");
    }

    private static async Task SkipAsync(Stream stream, long bytesToSkip, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        long remaining = bytesToSkip;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
                throw new IOException($"Stream ended prematurely while skipping: {remaining} of {bytesToSkip} bytes remaining.");
            remaining -= read;
        }
    }

    private static async Task CopyAsync(Stream source, FileStream destination, ProgressTracker progressTracker, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            progressTracker.AddProgress(bytesRead);
        }
    }

    private async Task<bool> VerifyChecksum(
        string snapshotFilePath, string snapshotChecksum, CancellationToken cancellationToken)
    {
        byte[] checksumBytes = Bytes.FromHexString(snapshotChecksum);
        if (_logger.IsInfo)
            _logger.Info($"Verifying snapshot checksum {snapshotChecksum}.");

        await using FileStream fileStream = File.OpenRead(snapshotFilePath);
        byte[] hash = await SHA256.HashDataAsync(fileStream, cancellationToken);
        return Bytes.AreEqual(hash, checksumBytes);
    }

    private Task ExtractSnapshotTo(string snapshotPath, string dbPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (_logger.IsInfo)
                _logger.Info($"Extracting snapshot to {dbPath}. Do not interrupt!");

            ZipFile.ExtractToDirectory(snapshotPath, dbPath);
        }, cancellationToken);

    private enum Stage
    {
        Start,
        Downloaded,
        Verified,
        Extracted,
        End,
    }

    private static void SetCheckpoint(ISnapshotConfig snapshotConfig, Stage stage)
    {
        string checkpointPath = Path.Combine(snapshotConfig.SnapshotDirectory, "checkpoint" + "_" + snapshotConfig.SnapshotFileName);
        File.WriteAllText(checkpointPath, stage.ToString());
    }

    private static Stage GetCheckpoint(ISnapshotConfig snapshotConfig)
    {
        string checkpointPath = Path.Combine(snapshotConfig.SnapshotDirectory, "checkpoint" + "_" + snapshotConfig.SnapshotFileName);
        if (File.Exists(checkpointPath))
        {
            string stringStage = File.ReadAllText(checkpointPath);
            return Enum.Parse<Stage>(stringStage);
        }
        else
        {
            return Stage.Start;
        }
    }
}
