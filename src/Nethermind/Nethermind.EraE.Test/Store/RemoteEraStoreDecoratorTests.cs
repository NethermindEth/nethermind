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
        (int epoch, string filename) = await StageRemoteEpochAsync(ctx);

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
        localStore.FindBlockAndReceipts(Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((null, null));

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RemoteEraEntry>());

        using RemoteEraStoreDecorator sut = CreateDecorator(localStore, maxEraSize: 16);

        Assert.That(async () => await sut.FindBlockAndReceipts(5, ensureValidated: false),
            Throws.TypeOf<EraException>().With.Message.Contains("Epoch 0").And.Message.Contains("not available"));
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

        Assert.That(async () => await sut.FindBlockAndReceipts(0, ensureValidated: false), Throws.TypeOf<EraVerificationException>().With.Message.Contains(@"SHA-256"));

        Assert.That(File.Exists(expectedFilePath), Is.False);
    }

    [TestCase(true, true, TestName = "FindBlockAndReceipts_WhenEnsureValidatedAndContentValid_ValidatesAndReturnsBlock")]
    [TestCase(false, true, TestName = "FindBlockAndReceipts_WhenEnsureValidatedAndContentInvalid_ThrowsAndDoesNotCache")]
    [TestCase(true, false, TestName = "FindBlockAndReceipts_WhenEnsureValidatedAndAccumulatorUntrusted_ThrowsAndDoesNotCache")]
    public async Task FindBlockAndReceipts_WhenEnsureValidated_EnforcesContentTrust(bool contentValid, bool accumulatorTrusted)
    {
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength: 32, from: 0, to: 0);
        (int epoch, string filename) = await StageRemoteEpochAsync(ctx);

        IBlockValidator blockValidator = contentValid ? Always.Valid : Always.Invalid;
        // A trusted set excluding the epoch's real accumulator root forces the untrusted-root rejection.
        ISet<ValueHash256>? trustedAccumulators = accumulatorTrusted
            ? null
            : new HashSet<ValueHash256> { new("0x1111111111111111111111111111111111111111111111111111111111111111") };
        using RemoteEraStoreDecorator sut = CreateDecorator(
            localStore: null, maxEraSize: 16, ctx.Resolve<ISpecProvider>(), blockValidator, trustedAccumulators);

        if (contentValid && accumulatorTrusted)
        {
            (Block? block, TxReceipt[]? receipts) = await sut.FindBlockAndReceipts(epoch * 16);
            Assert.That(block, Is.Not.Null);
            Assert.That(receipts, Is.Not.Null);
            Assert.That(block!.Number, Is.EqualTo(epoch * 16));
        }
        else
        {
            Assert.That(async () => await sut.FindBlockAndReceipts(epoch * 16), Throws.TypeOf<EraVerificationException>());
            // Rejected content caches nothing: the file is removed so a retry re-downloads.
            Assert.That(File.Exists(Path.Join(_downloadDir.Path, filename)), Is.False);
        }
    }

    [Test]
    public async Task FindBlockAndReceipts_WhenUnvalidatedReadPrecedesValidatedRead_StillRunsContentValidation()
    {
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength: 32, from: 0, to: 0);
        (int epoch, _) = await StageRemoteEpochAsync(ctx);

        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        blockValidator.ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out Arg.Any<string?>())
            .Returns(true);
        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16, ctx.Resolve<ISpecProvider>(), blockValidator);

        // An unvalidated read caches availability only and must not run content validation.
        await sut.FindBlockAndReceipts(epoch * 16, ensureValidated: false);
        blockValidator.DidNotReceiveWithAnyArgs().ValidateBodyAgainstHeader(default!, default!, out Arg.Any<string?>());

        // A later validated read on the same epoch must still run VerifyContent — the availability
        // cache alone cannot satisfy it.
        await sut.FindBlockAndReceipts(epoch * 16, ensureValidated: true);
        blockValidator.ReceivedWithAnyArgs().ValidateBodyAgainstHeader(default!, default!, out Arg.Any<string?>());
    }

    private RemoteEraStoreDecorator CreateDecorator(
        IEraStore? localStore, int maxEraSize, ISpecProvider? specProvider = null,
        IBlockValidator? blockValidator = null, ISet<ValueHash256>? trustedAccumulators = null) =>
        new(localStore, _client, _downloadDir.Path, maxEraSize,
            specProvider ?? Substitute.For<ISpecProvider>(), blockValidator ?? Always.Valid, trustedAccumulators);

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

    [Test]
    public async Task FindBlockAndReceipts_WhenManifestFilenameEscapesDownloadDir_ThrowsAndWritesNothing()
    {
        string filename = $"..{Path.DirectorySeparatorChar}escape-00000-deadbeef.erae";
        byte[] sha256 = new byte[32];

        _client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RemoteEraEntry> { [0] = new(filename, sha256) });

        string escapedPath = Path.GetFullPath(Path.Join(_downloadDir.Path, filename));
        using RemoteEraStoreDecorator sut = CreateDecorator(localStore: null, maxEraSize: 16);

        Assert.That(async () => await sut.FindBlockAndReceipts(0, ensureValidated: false), Throws.TypeOf<EraException>().With.Message.Contains("escapes the download directory"));

        await _client.DidNotReceive().DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.That(File.Exists(escapedPath), Is.False);
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
