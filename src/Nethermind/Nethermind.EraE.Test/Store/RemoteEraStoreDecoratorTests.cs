// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Autofac;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using EraException = Nethermind.Era1.Exceptions.EraException;
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
        (ulong epoch, string filename) = await StageRemoteEpochAsync(ctx);

        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16);

        (Block? block, TxReceipt[]? receipts) = await sut.FindBlockAndReceipts(epoch * 16, ensureValidated: false);

        Assert.That(block, Is.Not.Null);
        Assert.That(receipts, Is.Not.Null);
        Assert.That(block!.Number, Is.EqualTo(epoch * 16));
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

        Assert.That(block, Is.SameAs(expectedBlock));
        await _client.DidNotReceive().FetchManifestAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FindBlockAndReceipts_WhenEpochAbsentFromManifest_ThrowsEraException()
    {
        IEraStore localStore = Substitute.For<IEraStore>();
        localStore.FindBlockAndReceipts(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((null, null));

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<uint, RemoteEraEntry>());

        using RemoteEraStoreDecorator sut = CreateDecorator(localStore, maxEraSize: 16);

        Assert.That(async () => await sut.FindBlockAndReceipts(5UL, ensureValidated: false),
            Throws.TypeOf<EraException>().With.Message.Contains("Epoch 0").And.Message.Contains("not available"));
    }

    [Test]
    public async Task FindBlockAndReceipts_WhenDownloadedFileHasWrongChecksum_ThrowsAndDeletesFile()
    {
        string filename = "abc-00000-deadbeef.erae";
        byte[] wrongHash = new byte[32];

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<uint, RemoteEraEntry> { [0] = new(filename, wrongHash) });
        _client.DownloadFileAsync(filename, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                File.WriteAllBytes(callInfo.ArgAt<string>(1), [1, 2, 3, 4]);
                return Task.CompletedTask;
            });

        string expectedFilePath = Path.Join(_downloadDir.Path, filename);
        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16);

        Assert.That(async () => await sut.FindBlockAndReceipts(0UL, ensureValidated: false), Throws.TypeOf<EraVerificationException>().With.Message.Contains(@"SHA-256"));

        Assert.That(File.Exists(expectedFilePath), Is.False);
    }

    [TestCase(true, true, TestName = "FindBlockAndReceipts_WhenEnsureValidatedAndContentValid_ValidatesAndReturnsBlock")]
    [TestCase(false, true, TestName = "FindBlockAndReceipts_WhenEnsureValidatedAndContentInvalid_ThrowsAndDoesNotCache")]
    [TestCase(true, false, TestName = "FindBlockAndReceipts_WhenEnsureValidatedAndAccumulatorUntrusted_ThrowsAndDoesNotCache")]
    public async Task FindBlockAndReceipts_WhenEnsureValidated_EnforcesContentTrust(bool contentValid, bool accumulatorTrusted)
    {
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength: 32, from: 0, to: 0);
        (ulong epoch, string filename) = await StageRemoteEpochAsync(ctx);

        IBlockValidator blockValidator = contentValid ? Always.Valid : Always.Invalid;
        ISet<ValueHash256>? trustedAccumulators = accumulatorTrusted
            ? null
            : new HashSet<ValueHash256> { new("0x1111111111111111111111111111111111111111111111111111111111111111") };
        using RemoteEraStoreDecorator sut = CreateDecorator(
            localStore: null, maxEraSize: 16, ctx.Resolve<ISpecProvider>(), blockValidator, trustedAccumulators);

        ulong blockNumber = epoch * 16;
        if (contentValid && accumulatorTrusted)
        {
            (Block? block, TxReceipt[]? receipts) = await sut.FindBlockAndReceipts(blockNumber);
            Assert.That(block, Is.Not.Null);
            Assert.That(receipts, Is.Not.Null);
            Assert.That(block!.Number, Is.EqualTo(blockNumber));
        }
        else
        {
            Assert.That(async () => await sut.FindBlockAndReceipts(blockNumber), Throws.TypeOf<EraVerificationException>());
            Assert.That(File.Exists(Path.Join(_downloadDir.Path, filename)), Is.False);
        }
    }

    [Test]
    public async Task FindBlockAndReceipts_WhenUnvalidatedReadPrecedesValidatedRead_StillRunsContentValidation()
    {
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength: 32, from: 0, to: 0);
        (ulong epoch, _) = await StageRemoteEpochAsync(ctx);

        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        blockValidator.ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out Arg.Any<string?>())
            .Returns(true);
        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16, ctx.Resolve<ISpecProvider>(), blockValidator);

        ulong blockNumber = epoch * 16;
        await sut.FindBlockAndReceipts(blockNumber, ensureValidated: false);
        blockValidator.DidNotReceiveWithAnyArgs().ValidateBodyAgainstHeader(default!, default(BlockBody)!, out Arg.Any<string?>());

        await sut.FindBlockAndReceipts(blockNumber, ensureValidated: true);
        blockValidator.ReceivedWithAnyArgs().ValidateBodyAgainstHeader(default!, default(BlockBody)!, out Arg.Any<string?>());
    }

    private RemoteEraStoreDecorator CreateDecorator(
        IEraStore? localStore, ulong maxEraSize, ISpecProvider? specProvider = null,
        IBlockValidator? blockValidator = null, ISet<ValueHash256>? trustedAccumulators = null) =>
        new(localStore, _client, _downloadDir.Path, maxEraSize,
            specProvider ?? Substitute.For<ISpecProvider>(), blockValidator ?? Always.Valid, trustedAccumulators);

    private async Task<(ulong Epoch, string Filename)> StageRemoteEpochAsync(IContainer ctx)
    {
        string exportDir = ctx.ResolveTempDirPath();
        string eraFile = EraPathUtils.GetAllEraFiles(exportDir, EraETestModule.TestNetwork).First();
        string filename = Path.GetFileName(eraFile);
        ulong epoch = ParseEpoch(filename);
        byte[] sha256 = SHA256.HashData(await File.ReadAllBytesAsync(eraFile));

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<uint, RemoteEraEntry> { [(uint)epoch] = new(filename, sha256) });
        _client.DownloadFileAsync(filename, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CopyFile(eraFile, callInfo.ArgAt<string>(1)));

        return (epoch, filename);
    }

    [Test]
    public async Task FindBlockAndReceipts_WhenManifestFilenameEscapesDownloadDir_ThrowsAndWritesNothing()
    {
        string filename = $"..{Path.DirectorySeparatorChar}escape-00000-deadbeef.erae";
        byte[] sha256 = new byte[32];

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<uint, RemoteEraEntry> { [0] = new(filename, sha256) });

        string escapedPath = Path.GetFullPath(Path.Join(_downloadDir.Path, filename));
        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16);

        Assert.That(async () => await sut.FindBlockAndReceipts(0UL, ensureValidated: false), Throws.TypeOf<EraException>().With.Message.Contains("escapes the download directory"));

        await _client.DidNotReceive().DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.That(File.Exists(escapedPath), Is.False);
    }

    private static ulong ParseEpoch(string filename)
    {
        string[] parts = Path.GetFileNameWithoutExtension(filename).Split('-');
        return ulong.Parse(parts[1]);
    }

    private static Task CopyFile(string source, string destination)
    {
        File.Copy(source, destination);
        return Task.CompletedTask;
    }
}
