// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Init;
using Nethermind.Logging;

namespace Nethermind.InvalidChainDump;

/// <summary>
/// Zips the diagnostics of each dumped invalid block with a manifest and uploads the archive to an <see cref="IDumpArchiveStore"/>.
/// </summary>
/// <remarks>
/// Archives are built and uploaded one at a time, off the dump thread. A failed or timed-out upload is logged and
/// leaves its archive in the temporary directory; it does not stop the uploads that follow.
/// Disposal lets pending uploads finish for a bounded time, so the dump of the block a node exits on can still be uploaded.
/// </remarks>
internal sealed class InvalidChainDumpUploader : IInvalidBlockDumpHandler, IDisposable
{
    internal const string ManifestFileName = "manifest.txt";
    private const int QueueCapacity = 16;
    private static readonly TimeSpan CompletionWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CancellationWait = TimeSpan.FromSeconds(5);

    private readonly IDumpArchiveStore _store;
    private readonly TimeSpan _uploadTimeout;
    private readonly ILogger _logger;
    private readonly Channel<(Block Block, IReadOnlyList<string> Files)> _queue =
        Channel.CreateBounded<(Block, IReadOnlyList<string>)>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;

    public InvalidChainDumpUploader(IInvalidChainDumpConfig config, IDumpArchiveStore store, ILogManager logManager)
    {
        if (config.UploadTimeoutMs <= 0)
        {
            throw new InvalidConfigurationException($"{nameof(IInvalidChainDumpConfig)}.{nameof(config.UploadTimeoutMs)} must be positive.", ExitCodes.ForbiddenOptionValue);
        }

        _store = store;
        _uploadTimeout = TimeSpan.FromMilliseconds(config.UploadTimeoutMs);
        _logger = logManager.GetClassLogger<InvalidChainDumpUploader>();
        _worker = Task.Run(ProcessQueueAsync);
    }

    public void OnDumped(Block block, IReadOnlyList<string> filePaths)
    {
        if (!_queue.Writer.TryWrite((block, filePaths)) && _logger.IsWarn)
        {
            _logger.Warn($"Skipped upload of diagnostics of invalid block {block.ToString(Block.Format.FullHashAndNumber)}: the upload queue is full or shut down.");
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach ((Block block, IReadOnlyList<string> files) in _queue.Reader.ReadAllAsync(_cancellation.Token))
            {
                await UploadAsync(block, files, _cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task UploadAsync(Block block, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        string archivePath = Path.Combine(Path.GetTempPath(), $"dump-{block.Hash}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.zip");
        try
        {
            await CreateArchiveAsync(block, files, archivePath, cancellationToken);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_uploadTimeout);
            string key = await _store.UploadAsync(archivePath, timeout.Token);

            File.Delete(archivePath);
            if (_logger.IsInfo) _logger.Info($"Uploaded diagnostics of invalid block {block.ToString(Block.Format.FullHashAndNumber)} as {key}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsWarn) _logger.Warn($"Upload of diagnostics of invalid block {block.ToString(Block.Format.FullHashAndNumber)} stopped by shutdown; the archive is kept at {archivePath:hide}");
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Failed to upload diagnostics of invalid block {block.ToString(Block.Format.FullHashAndNumber)} from {archivePath:hide}", exception);
        }
    }

    private static async Task CreateArchiveAsync(Block block, IReadOnlyList<string> files, string archivePath, CancellationToken cancellationToken)
    {
        await using ZipArchive archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Create, cancellationToken);
        foreach (string file in files)
        {
            await archive.CreateEntryFromFileAsync(file, Path.GetFileName(file), CompressionLevel.Optimal, cancellationToken);
        }

        await using StreamWriter manifest = new(await archive.CreateEntry(ManifestFileName).OpenAsync(cancellationToken));
        manifest.WriteLine($"hash={block.Hash}");
        manifest.WriteLine($"number={block.Number}");
        manifest.WriteLine($"parent={block.ParentHash}");
        manifest.WriteLine($"transactions={block.Transactions.Length}");
        manifest.WriteLine($"timestamp={block.Timestamp}");
        manifest.WriteLine($"client={ProductInfo.ClientId}");
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        if (!_worker.Wait(CompletionWait)) _cancellation.Cancel();
        if (!_worker.Wait(CancellationWait))
        {
            if (_logger.IsWarn) _logger.Warn("Upload of invalid-block diagnostics did not stop in time.");
            return;
        }

        _cancellation.Dispose();
    }
}
