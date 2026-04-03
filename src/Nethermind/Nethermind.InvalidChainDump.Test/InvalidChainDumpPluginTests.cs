// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.InvalidChainDump.Test;

public class InvalidChainDumpPluginTests
{
    [Test]
    public async Task Init_enables_required_auto_dump_flags_when_direct_s3_upload_is_configured()
    {
        IInitConfig initConfig = new InitConfig() { AutoDump = DumpOptions.None };
        IInvalidChainDumpConfig invalidChainDumpConfig = new InvalidChainDumpConfig()
        {
            ServiceUrl = "http://127.0.0.1:9000",
            BucketName = "fails",
            AccessKey = "minioadmin",
            SecretKey = "secret",
        };
        NethermindApi api = BuildApi(initConfig, invalidChainDumpConfig, Substitute.For<IReceiptFinder>(), Substitute.For<IBlockchainProcessor>());

        await using InvalidChainDumpPlugin plugin = new(invalidChainDumpConfig);

        await plugin.Init(api);

        initConfig.AutoDump.Should().HaveFlag(DumpOptions.Rlp);
        initConfig.AutoDump.Should().HaveFlag(DumpOptions.Receipts);
        initConfig.AutoDump.Should().HaveFlag(DumpOptions.Parity);
        initConfig.AutoDump.Should().HaveFlag(DumpOptions.Geth);
    }

    [Test]
    public async Task Invalid_block_creates_archive_with_expected_artifacts()
    {
        using TempPath tempDirectory = TempPath.GetTempDirectory();
        BlockHeader header = Build.A.BlockHeader.WithNumber(7).WithHash(TestItem.KeccakA).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;

        TxReceipt[] receipts =
        [
            Build.A.Receipt
                .WithBlockHash(block.Hash!)
                .WithBlockNumber(block.Number)
                .WithIndex(0)
                .TestObject
        ];

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(block, Arg.Any<bool>(), Arg.Any<bool>()).Returns(receipts);

        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        IInvalidChainDumpConfig invalidChainDumpConfig = new InvalidChainDumpConfig()
        {
            ServiceUrl = "http://127.0.0.1:9000",
            BucketName = "fails",
            AccessKey = "minioadmin",
            SecretKey = "secret",
        };
        IInitConfig initConfig = new InitConfig() { AutoDump = DumpOptions.None };
        CapturingUploader uploader = new();
        NethermindApi api = BuildApi(initConfig, invalidChainDumpConfig, receiptFinder, blockchainProcessor);

        File.WriteAllText(BlockTraceDumper.GetDiagnosticFilePath(BlockTraceDumper.GetParityTraceFileName(block.Hash!, isSuccess: false)), "parity");
        File.WriteAllText(BlockTraceDumper.GetDiagnosticFilePath(BlockTraceDumper.GetGethTraceFileName(block.Hash!, isSuccess: false)), "geth");
        File.WriteAllText(BlockTraceDumper.GetDiagnosticFilePath(BlockTraceDumper.GetReceiptsTraceFileName(block.Hash!, isSuccess: false)), "receipts");

        await using InvalidChainDumpPlugin plugin = new(invalidChainDumpConfig, _ => uploader, tempDirectory.Path);
        await plugin.Init(api);

        blockchainProcessor.InvalidBlock += Raise.EventWith(
            this,
            new IBlockchainProcessor.InvalidBlockEventArgs { InvalidBlock = block });

        byte[] archiveBytes = await uploader.WaitForUploadAsync();

        using MemoryStream memoryStream = new(archiveBytes);
        using ZipArchive zipArchive = new(memoryStream, ZipArchiveMode.Read);

        zipArchive.Entries.Select(static entry => entry.FullName).Should().Contain(BlockTraceDumper.GetInvalidBlockRlpFileName(block.Hash!));
        zipArchive.Entries.Select(static entry => entry.FullName).Should().Contain($"receipts_{block.Hash}.rlp");
        zipArchive.Entries.Select(static entry => entry.FullName).Should().Contain(BlockTraceDumper.GetParityTraceFileName(block.Hash!, isSuccess: false));
        zipArchive.Entries.Select(static entry => entry.FullName).Should().Contain(BlockTraceDumper.GetGethTraceFileName(block.Hash!, isSuccess: false));
        zipArchive.Entries.Select(static entry => entry.FullName).Should().Contain(BlockTraceDumper.GetReceiptsTraceFileName(block.Hash!, isSuccess: false));
        zipArchive.Entries.Select(static entry => entry.FullName).Should().Contain("manifest.txt");
    }

    private static NethermindApi BuildApi(
        IInitConfig initConfig,
        IInvalidChainDumpConfig invalidChainDumpConfig,
        IReceiptFinder receiptFinder,
        IBlockchainProcessor blockchainProcessor)
    {
        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockchainProcessor.Returns(blockchainProcessor);

        IConfigProvider configProvider = new ConfigProvider(initConfig, invalidChainDumpConfig);
        ILifetimeScope container = new ContainerBuilder()
            .AddSingleton(mainProcessingContext)
            .AddSingleton(receiptFinder)
            .Build();

        NethermindApi.Dependencies dependencies = new(
            configProvider,
            new EthereumJsonSerializer(),
            LimboLogs.Instance,
            new ChainSpec { Parameters = new ChainParameters(), },
            MainnetSpecProvider.Instance,
            [],
            Substitute.For<IProcessExitSource>(),
            container);

        return new NethermindApi(dependencies);
    }

    private sealed class CapturingUploader : IInvalidChainDumpUploader
    {
        private readonly TaskCompletionSource<byte[]> _taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task UploadAsync(S3UploadTarget uploadTarget, string archivePath, string objectKey, CancellationToken cancellationToken)
        {
            _taskCompletionSource.TrySetResult(File.ReadAllBytes(archivePath));
            return Task.CompletedTask;
        }

        public async Task<byte[]> WaitForUploadAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            Task completedTask = await Task.WhenAny(_taskCompletionSource.Task, Task.Delay(Timeout.Infinite, timeout.Token));
            if (completedTask != _taskCompletionSource.Task)
            {
                throw new TimeoutException("Timed out waiting for diagnostic archive upload.");
            }

            return await _taskCompletionSource.Task;
        }
    }
}
