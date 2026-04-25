// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using EraException = Nethermind.Era1.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Store;

public class RemoteEraStoreDecoratorTests
{
    private IRemoteEraClient _client = null!;
    private TempPath _downloadDir = null!;

    [SetUp]
    public void SetUp()
    {
        _client = Substitute.For<IRemoteEraClient>();
        _downloadDir = TempPath.GetTempDirectory();
    }

    [TearDown]
    public void TearDown() => _downloadDir.Dispose();

    [Test]
    public async Task FindBlockAndReceipts_WhenEpochMissingLocally_DownloadsAndReturnsBlock()
    {
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength: 32, from: 0, to: 0);
        string exportDir = ctx.ResolveTempDirPath();
        string eraFile = EraPathUtils.GetAllEraFiles(exportDir, EraETestModule.TestNetwork).First();
        string filename = Path.GetFileName(eraFile);
        int epoch = ParseEpoch(filename);
        byte[] sha256 = SHA256.HashData(await File.ReadAllBytesAsync(eraFile));

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RemoteEraEntry> { [epoch] = new(filename, sha256) });
        _client.DownloadFileAsync(filename, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CopyFile(eraFile, callInfo.ArgAt<string>(1)));

        using RemoteEraStoreDecorator sut = new(localStore: null, _client, _downloadDir.Path, maxEraSize: 16);

        (Block? block, TxReceipt[]? receipts) = await sut.FindBlockAndReceipts(epoch * 16, ensureValidated: false);

        block.Should().NotBeNull();
        receipts.Should().NotBeNull();
        block!.Number.Should().Be(epoch * 16);
        await _client.Received(1).DownloadFileAsync(filename, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FindBlockAndReceipts_WhenLocalStoreReturnsBlock_SkipsRemoteClient()
    {
        Block expectedBlock = Build.A.Block.WithNumber(42).TestObject;
        TxReceipt[] expectedReceipts = [];

        IEraStore localStore = Substitute.For<IEraStore>();
        localStore.FindBlockAndReceipts(42, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((expectedBlock, (TxReceipt[]?)expectedReceipts));

        using RemoteEraStoreDecorator sut = new(localStore, _client, _downloadDir.Path, maxEraSize: 16);

        (Block? block, TxReceipt[]? _) = await sut.FindBlockAndReceipts(42, ensureValidated: false);

        block.Should().BeSameAs(expectedBlock);
        await _client.DidNotReceive().FetchManifestAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FindBlockAndReceipts_WhenEpochAbsentFromManifest_ThrowsEraException()
    {
        IEraStore localStore = Substitute.For<IEraStore>();
        localStore.FindBlockAndReceipts(Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((null, null));

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RemoteEraEntry>());

        using RemoteEraStoreDecorator sut = new(localStore, _client, _downloadDir.Path, maxEraSize: 16);

        await sut.Invoking(s => s.FindBlockAndReceipts(5, ensureValidated: false))
            .Should().ThrowAsync<EraException>()
            .WithMessage("*Epoch 0*not available*");
    }

    [Test]
    public async Task FindBlockAndReceipts_WhenDownloadedFileHasWrongChecksum_ThrowsAndDeletesFile()
    {
        string filename = "abc-00000-deadbeef.erae";
        byte[] wrongHash = new byte[32];

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RemoteEraEntry> { [0] = new(filename, wrongHash) });
        _client.DownloadFileAsync(filename, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                File.WriteAllBytes(callInfo.ArgAt<string>(1), [1, 2, 3, 4]);
                return Task.CompletedTask;
            });

        string expectedFilePath = Path.Join(_downloadDir.Path, filename);
        using RemoteEraStoreDecorator sut = new(localStore: null, _client, _downloadDir.Path, maxEraSize: 16);

        await sut.Invoking(s => s.FindBlockAndReceipts(0, ensureValidated: false))
            .Should().ThrowAsync<EraVerificationException>()
            .WithMessage("*SHA-256*");

        File.Exists(expectedFilePath).Should().BeFalse();
    }

    private static int ParseEpoch(string filename)
    {
        string[] parts = Path.GetFileNameWithoutExtension(filename).Split('-');
        return int.Parse(parts[1]);
    }

    private static Task CopyFile(string source, string destination)
    {
        File.Copy(source, destination);
        return Task.CompletedTask;
    }
}
