// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using System.IO.Compression;
using System.Security.Cryptography;
using Nethermind.Core.Extensions;

namespace Nethermind.Init.Snapshot;

public class InitDatabaseSnapshot : InitDatabase
{
    private const int BufferSize = 8192;

    private readonly INethermindApi _api;
    private readonly ILogger _logger;

    public InitDatabaseSnapshot(INethermindApi api) : base(api)
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
        string dbPath = _api.Config<IInitConfig>().BaseDbPath;
        if (Path.Exists(dbPath))
        {
            if (_logger.IsInfo)
                _logger.Info($"Database already exists at {dbPath}. Skipping snapshot initialization.");
            return;
        }

        ISnapshotConfig snapshotConfig = _api.Config<ISnapshotConfig>();
        string snapshotUrl = snapshotConfig.DownloadUrl ??
                             throw new InvalidOperationException("Snapshot download URL is not configured");
        byte[]? snapshotChecksum =
            snapshotConfig.Checksum is null ? null : Bytes.FromHexString(snapshotConfig.Checksum);

        string snapshotFileName = Path.GetTempFileName();

        await DownloadSnapshotTo(snapshotUrl, snapshotFileName, cancellationToken);

        if (snapshotChecksum is not null)
        {
            bool isChecksumValid = await VerifyChecksum(snapshotFileName, snapshotChecksum);
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


        await ExtractSnapshotTo(snapshotFileName, dbPath, cancellationToken);

        if (_logger.IsInfo)
            _logger.Info("Database successfully initialized from snapshot.");
    }

    private async Task DownloadSnapshotTo(
        string snapshotUrl, string snapshotFileName, CancellationToken cancellationToken)
    {
        if (_logger.IsInfo)
            _logger.Info($"Downloading snapshot from {snapshotUrl}");

        using HttpClient httpClient = new();

        HttpRequestMessage request = new(HttpMethod.Get, snapshotUrl);
        FileInfo snapshotFileInfo = new(snapshotFileName);
        if (snapshotFileInfo.Exists)
            request.Headers.Range = new RangeHeaderValue(snapshotFileInfo.Length, null);

        using HttpResponseMessage response =
            (await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            .EnsureSuccessStatusCode();

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream snapshotFileStream = new(
            snapshotFileName, FileMode.Append, FileAccess.Write, FileShare.None, BufferSize, true);

        long totalBytesRead = snapshotFileInfo.Length;
        long? totalBytesToRead = response.Content.Headers.ContentLength;

        using ProgressTracker progressTracker = new(
            _api.LogManager, _api.TimerFactory, TimeSpan.FromSeconds(5), totalBytesRead, totalBytesToRead);

        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int bytesRead = await contentStream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                break;
            await snapshotFileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

            progressTracker.AddProgress(bytesRead);
        }

        if (_logger.IsInfo)
            _logger.Info($"Snapshot downloaded to {snapshotFileName}.");
    }

    private async Task<bool> VerifyChecksum(string snapshotFilePath, byte[] snapshotChecksum)
    {
        if (_logger.IsInfo)
            _logger.Info($"Verifying snapshot checksum {snapshotChecksum}.");

        await using FileStream fileStream = File.OpenRead(snapshotFilePath);
        byte[] hash = await SHA256.HashDataAsync(fileStream);
        return Bytes.AreEqual(hash, snapshotChecksum);
    }

    private Task ExtractSnapshotTo(string snapshotPath, string dbPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (_logger.IsInfo)
                _logger.Info($"Extracting snapshot to {dbPath}.");

            ZipFile.ExtractToDirectory(snapshotPath, dbPath);
        }, cancellationToken);
}
