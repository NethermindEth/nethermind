// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.InvalidChainDump;

/// <summary>
/// Uploads invalid-block diagnostics as zip archives to a remote object store.
/// </summary>
public sealed class InvalidChainDumpPlugin : INethermindPlugin, IAsyncDisposable
{
    private readonly IInvalidChainDumpConfig _invalidChainDumpConfig;
    private readonly Func<S3UploadTarget, IInvalidChainDumpUploader> _uploaderFactory;
    private readonly string _diagnosticDirectory;
    private IBlockchainProcessor? _blockchainProcessor;
    private InvalidChainDumpWorker? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidChainDumpPlugin"/> class.
    /// </summary>
    /// <param name="invalidChainDumpConfig">The plugin configuration.</param>
    public InvalidChainDumpPlugin(IInvalidChainDumpConfig invalidChainDumpConfig)
        : this(invalidChainDumpConfig, CreateUploader, Path.GetTempPath())
    {
    }

    internal InvalidChainDumpPlugin(
        IInvalidChainDumpConfig invalidChainDumpConfig,
        Func<S3UploadTarget, IInvalidChainDumpUploader> uploaderFactory,
        string diagnosticDirectory)
    {
        ArgumentNullException.ThrowIfNull(invalidChainDumpConfig);
        ArgumentNullException.ThrowIfNull(uploaderFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticDirectory);

        _invalidChainDumpConfig = invalidChainDumpConfig;
        _uploaderFactory = uploaderFactory;
        _diagnosticDirectory = diagnosticDirectory;
    }

    public string Name => "InvalidChainDump";

    public string Description => "Uploads invalid-block diagnostic bundles.";

    public string Author => "Nethermind";

    public bool Enabled => HasDirectS3Configuration(_invalidChainDumpConfig);

    public Task Init(INethermindApi nethermindApi)
    {
        ArgumentNullException.ThrowIfNull(nethermindApi);
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        S3UploadTarget? uploadTarget = TryCreateUploadTarget(_invalidChainDumpConfig);
        if (uploadTarget is null)
        {
            ILogger logger = nethermindApi.LogManager.GetClassLogger<InvalidChainDumpPlugin>();
            if (logger.IsWarn) logger.Warn("Invalid invalid-chain dump configuration. Configure direct S3-compatible settings.");
            return Task.CompletedTask;
        }

        IInitConfig initConfig = nethermindApi.Config<IInitConfig>();
        initConfig.AutoDump |= DumpOptions.Rlp | DumpOptions.Receipts | DumpOptions.Parity | DumpOptions.Geth;

        ILogger workerLogger = nethermindApi.LogManager.GetClassLogger<InvalidChainDumpPlugin>();
        _worker = new InvalidChainDumpWorker(
            nethermindApi.ReceiptFinder,
            _uploaderFactory(uploadTarget),
            workerLogger,
            uploadTarget,
            _invalidChainDumpConfig.UploadTimeoutMilliseconds,
            _diagnosticDirectory);

        _blockchainProcessor = nethermindApi.MainProcessingContext.BlockchainProcessor;
        _blockchainProcessor.InvalidBlock += OnInvalidBlock;
        return Task.CompletedTask;
    }

    private void OnInvalidBlock(object? sender, IBlockchainProcessor.InvalidBlockEventArgs args)
    {
        _worker?.Enqueue(args.InvalidBlock);
    }

    public async ValueTask DisposeAsync()
    {
        if (_blockchainProcessor is not null)
        {
            _blockchainProcessor.InvalidBlock -= OnInvalidBlock;
        }

        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }
    }

    private static IInvalidChainDumpUploader CreateUploader(S3UploadTarget _) => new S3InvalidChainDumpUploader();

    internal static S3UploadTarget? TryCreateUploadTarget(IInvalidChainDumpConfig invalidChainDumpConfig)
    {
        if (!HasDirectS3Configuration(invalidChainDumpConfig))
        {
            return null;
        }

        if (!Uri.TryCreate(invalidChainDumpConfig.ServiceUrl, UriKind.Absolute, out Uri? serviceUri))
        {
            return null;
        }

        return new S3UploadTarget(
            serviceUri,
            invalidChainDumpConfig.BucketName,
            invalidChainDumpConfig.AccessKey,
            invalidChainDumpConfig.SecretKey,
            invalidChainDumpConfig.Region,
            invalidChainDumpConfig.ObjectKeyPrefix);
    }

    private static bool HasDirectS3Configuration(IInvalidChainDumpConfig invalidChainDumpConfig) =>
        !string.IsNullOrWhiteSpace(invalidChainDumpConfig.ServiceUrl)
        && !string.IsNullOrWhiteSpace(invalidChainDumpConfig.BucketName)
        && !string.IsNullOrWhiteSpace(invalidChainDumpConfig.AccessKey)
        && !string.IsNullOrWhiteSpace(invalidChainDumpConfig.SecretKey);
}

internal interface IInvalidChainDumpUploader
{
    Task UploadAsync(S3UploadTarget uploadTarget, string archivePath, string objectKey, CancellationToken cancellationToken);
}

internal sealed class InvalidChainDumpWorker : IAsyncDisposable
{
    private readonly IReceiptFinder _receiptFinder;
    private readonly IInvalidChainDumpUploader _uploader;
    private readonly ILogger _logger;
    private readonly S3UploadTarget _uploadTarget;
    private readonly string _diagnosticDirectory;
    private readonly int _uploadTimeoutMilliseconds;
    private readonly Channel<Block> _queue = Channel.CreateUnbounded<Block>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _workerTask;

    public InvalidChainDumpWorker(
        IReceiptFinder receiptFinder,
        IInvalidChainDumpUploader uploader,
        ILogger logger,
        S3UploadTarget uploadTarget,
        int uploadTimeoutMilliseconds,
        string diagnosticDirectory)
    {
        ArgumentNullException.ThrowIfNull(receiptFinder);
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentNullException.ThrowIfNull(uploadTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticDirectory);

        _receiptFinder = receiptFinder;
        _uploader = uploader;
        _logger = logger;
        _uploadTarget = uploadTarget;
        _uploadTimeoutMilliseconds = uploadTimeoutMilliseconds;
        _diagnosticDirectory = diagnosticDirectory;
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void Enqueue(Block block)
    {
        if (!_queue.Writer.TryWrite(block) && _logger.IsWarn)
        {
            _logger.Warn($"Failed to enqueue invalid block diagnostic upload for {block.Hash}");
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (Block block in _queue.Reader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                await CreateAndUploadArchiveAsync(block, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Invalid-chain dump worker failed.", ex);
        }
    }

    private async Task CreateAndUploadArchiveAsync(Block block, CancellationToken cancellationToken)
    {
        Hash256 blockHash = block.Hash ?? throw new InvalidOperationException("Invalid block dump requires a resolved hash.");
        string suffix = $"{blockHash}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        string stagingDirectory = Path.Combine(_diagnosticDirectory, $"dump-{suffix}");
        string archivePath = Path.Combine(_diagnosticDirectory, $"dump-{suffix}.zip");
        string objectKey = BuildObjectKey(suffix);

        Directory.CreateDirectory(stagingDirectory);

        try
        {
            CopyExistingDiagnosticFiles(blockHash, stagingDirectory);
            WriteBlockRlp(block, stagingDirectory);
            WriteReceiptRlp(block, stagingDirectory);
            WriteManifest(block, stagingDirectory);

            ZipFile.CreateFromDirectory(stagingDirectory, archivePath, CompressionLevel.SmallestSize, includeBaseDirectory: false);

            using CancellationTokenSource timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(_uploadTimeoutMilliseconds);
            await _uploader.UploadAsync(_uploadTarget, archivePath, objectKey, timeoutCancellation.Token);

            if (_logger.IsInfo)
            {
                _logger.Info($"Uploaded invalid block diagnostic archive for {blockHash} as {objectKey}");
            }

            File.Delete(archivePath);
            Directory.Delete(stagingDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Failed to upload invalid block diagnostic archive for {blockHash}. Archive kept at {archivePath}", ex);
            }
        }
    }

    private void CopyExistingDiagnosticFiles(Hash256 blockHash, string stagingDirectory)
    {
        foreach (string diagnosticPath in BlockTraceDumper.GetInvalidBlockDiagnosticPaths(blockHash))
        {
            if (!File.Exists(diagnosticPath))
            {
                continue;
            }

            string destinationPath = Path.Combine(stagingDirectory, Path.GetFileName(diagnosticPath));
            File.Copy(diagnosticPath, destinationPath, overwrite: true);
        }
    }

    private static void WriteBlockRlp(Block block, string stagingDirectory)
    {
        Rlp rlp = new BlockDecoder().Encode(block, RlpBehaviors.AllowExtraBytes);
        string outputPath = Path.Combine(stagingDirectory, BlockTraceDumper.GetInvalidBlockRlpFileName(block.Hash!));
        File.WriteAllBytes(outputPath, rlp.Bytes);
    }

    private void WriteReceiptRlp(Block block, string stagingDirectory)
    {
        TxReceipt[] receipts = _receiptFinder.Get(block);
        ReceiptArrayStorageDecoder receiptArrayStorageDecoder = new();
        RlpStream stream = new(receiptArrayStorageDecoder.GetLength(receipts, RlpBehaviors.Storage));
        receiptArrayStorageDecoder.Encode(stream, receipts, RlpBehaviors.Storage);

        string outputPath = Path.Combine(stagingDirectory, $"receipts_{block.Hash}.rlp");
        File.WriteAllBytes(outputPath, stream.Data.ToArray() ?? []);
    }

    private static void WriteManifest(Block block, string stagingDirectory)
    {
        List<string> lines =
        [
            $"hash={block.Hash}",
            $"number={block.Number}",
            $"parent={block.ParentHash}",
            $"transactions={block.Transactions.Length}",
            $"timestamp={block.Timestamp}"
        ];

        File.WriteAllLines(Path.Combine(stagingDirectory, "manifest.txt"), lines);
    }

    private string BuildObjectKey(string suffix)
    {
        string fileName = $"dump-{suffix}.zip";
        string prefix = _uploadTarget.ObjectKeyPrefix.Trim('/');
        return string.IsNullOrWhiteSpace(prefix)
            ? fileName
            : $"{prefix}/{fileName}";
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cancellationTokenSource.Cancel();

        try
        {
            await _workerTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }
}

internal sealed class S3InvalidChainDumpUploader : IInvalidChainDumpUploader
{
    public async Task UploadAsync(S3UploadTarget uploadTarget, string archivePath, string objectKey, CancellationToken cancellationToken)
    {
        AmazonS3Config config = new()
        {
            AuthenticationRegion = uploadTarget.Region,
            ForcePathStyle = true,
            ServiceURL = uploadTarget.ServiceUri.ToString(),
        };

        using AmazonS3Client client = new(
            new BasicAWSCredentials(uploadTarget.AccessKey, uploadTarget.SecretKey),
            config);

        PutObjectRequest request = new()
        {
            BucketName = uploadTarget.BucketName,
            Key = objectKey,
            FilePath = archivePath,
            ContentType = "application/zip",
        };

        await client.PutObjectAsync(request, cancellationToken);
    }
}

internal sealed record S3UploadTarget(
    Uri ServiceUri,
    string BucketName,
    string AccessKey,
    string SecretKey,
    string Region,
    string ObjectKeyPrefix);
