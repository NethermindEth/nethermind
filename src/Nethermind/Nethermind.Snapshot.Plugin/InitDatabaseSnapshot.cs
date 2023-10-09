// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using System;
using System.IO.Compression;

namespace Nethermind.Snapshot.Plugin;

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
        string snapshotUrl = _api.Config<ISnapshotConfig>().DownloadUrl ??
                             throw new InvalidOperationException("Snapshot download URL is not configured");

        IInitConfig initConfig = _api.Config<IInitConfig>();
        switch (initConfig.DiagnosticMode)
        {
            case DiagnosticMode.RpcDb:
            case DiagnosticMode.ReadOnlyDb:
            case DiagnosticMode.MemDb:
                break;
            default:
                await InitDbFromSnapshot(snapshotUrl, initConfig.BaseDbPath, cancellationToken);
                break;
        }

        await base.Execute(cancellationToken);
    }

    private async Task InitDbFromSnapshot(string snapshotUrl, string dbPath, CancellationToken cancellationToken)
    {
        string snapshotFileName = Path.GetTempFileName();

        if (_logger.IsInfo)
            _logger.Info($"Downloading snapshot from {snapshotUrl}");

        await DownloadSnapshotTo(snapshotUrl, snapshotFileName, cancellationToken);

        if (_logger.IsInfo)
            _logger.Info($"Snapshot downloaded to {snapshotFileName}");

        try
        {
            Directory.Delete(dbPath, true);
        }
        catch (DirectoryNotFoundException)
        {
            // ignore
        }

        await ExtractZipAsync(snapshotFileName, dbPath, cancellationToken);
    }

    private async Task DownloadSnapshotTo(
        string snapshotUrl, string snapshotFileName, CancellationToken cancellationToken)
    {
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
    }

    private static Task ExtractZipAsync(string zipPath, string destinationPath, CancellationToken cancellationToken) =>
        Task.Run(() => ZipFile.ExtractToDirectory(zipPath, destinationPath, true), cancellationToken);
}
