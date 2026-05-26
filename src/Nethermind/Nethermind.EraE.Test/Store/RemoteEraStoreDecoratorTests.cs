// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Autofac;
using FluentAssertions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
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
        (int epoch, string filename) = await StageRemoteEpochAsync(ctx);

        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16);

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

        using RemoteEraStoreDecorator sut = CreateDecorator(localStore, maxEraSize: 16);

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

        using RemoteEraStoreDecorator sut = CreateDecorator(localStore, maxEraSize: 16);

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
        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16);

        await sut.Invoking(s => s.FindBlockAndReceipts(0, ensureValidated: false))
            .Should().ThrowAsync<EraVerificationException>()
            .WithMessage("*SHA-256*");

        File.Exists(expectedFilePath).Should().BeFalse();
    }

    // ensureValidated (the default) must run EraReader.VerifyContent on the remote path, matching
    // EraStore: valid content is returned, invalid content throws and is not cached as verified —
    // a manifest-matching SHA-256 alone is not enough.
    [TestCase(true, TestName = "FindBlockAndReceipts_WhenEnsureValidatedAndContentValid_ValidatesAndReturnsBlock")]
    [TestCase(false, TestName = "FindBlockAndReceipts_WhenEnsureValidatedAndContentInvalid_ThrowsAndDoesNotCache")]
    public async Task FindBlockAndReceipts_WhenEnsureValidated_RunsContentValidation(bool contentValid)
    {
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength: 32, from: 0, to: 0);
        (int epoch, string filename) = await StageRemoteEpochAsync(ctx);

        IBlockValidator blockValidator = contentValid ? Always.Valid : Always.Invalid;
        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16, ctx.Resolve<ISpecProvider>(), blockValidator);

        if (contentValid)
        {
            (Block? block, TxReceipt[]? receipts) = await sut.FindBlockAndReceipts(epoch * 16);
            block.Should().NotBeNull();
            receipts.Should().NotBeNull();
            block!.Number.Should().Be(epoch * 16);
        }
        else
        {
            await sut.Invoking(s => s.FindBlockAndReceipts(epoch * 16))
                .Should().ThrowAsync<EraVerificationException>();
            // Failed validation caches nothing: the file is removed so a retry re-downloads.
            File.Exists(Path.Join(_downloadDir.Path, filename)).Should().BeFalse();
        }
    }

    private RemoteEraStoreDecorator CreateDecorator(
        IEraStore? localStore, int maxEraSize, ISpecProvider? specProvider = null, IBlockValidator? blockValidator = null) =>
        new(localStore, _client, _downloadDir.Path, maxEraSize,
            specProvider ?? Substitute.For<ISpecProvider>(), blockValidator ?? Always.Valid);

    // Wires the mock client to serve a freshly exported era file (with a matching SHA-256) and
    // returns its epoch and filename.
    private async Task<(int Epoch, string Filename)> StageRemoteEpochAsync(IContainer ctx)
    {
        string exportDir = ctx.ResolveTempDirPath();
        string eraFile = EraPathUtils.GetAllEraFiles(exportDir, EraETestModule.TestNetwork).First();
        string filename = Path.GetFileName(eraFile);
        int epoch = ParseEpoch(filename);
        byte[] sha256 = SHA256.HashData(await File.ReadAllBytesAsync(eraFile));

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RemoteEraEntry> { [epoch] = new(filename, sha256) });
        _client.DownloadFileAsync(filename, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CopyFile(eraFile, callInfo.ArgAt<string>(1)));

        return (epoch, filename);
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
