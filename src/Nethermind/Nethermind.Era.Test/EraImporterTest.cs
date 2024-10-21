// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using Nethermind.Synchronization;
using NSubstitute;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Nethermind.Core;
using Nethermind.Era1.Exceptions;
using Nethermind.Logging;

namespace Nethermind.Era1.Test;
public class EraImporterTest
{
    [Test]
    public void ImportAsArchiveSync_DirectoryContainsNoEraFiles_ThrowEraImportException()
    {
        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        IFileSystem fileSystem = new MockFileSystem();
        IDirectoryInfo tempDirectory = fileSystem.Directory.CreateTempSubdirectory();
        EraImporter sut = new(fileSystem,
                              blockTree,
                              Substitute.For<IBlockValidator>(),
                              Substitute.For<IReceiptStorage>(),
                              Substitute.For<ISpecProvider>(),
                              LimboLogs.Instance,
                              "abc");

        Assert.That(() => sut.ImportAsArchiveSync(tempDirectory.FullName, CancellationToken.None), Throws.TypeOf<EraImportException>());
        tempDirectory.Delete();
    }

    [Test]
    public void ImportAsArchiveSync_DirectoryContainsWrongEraFiles_ThrowEraImportException()
    {
        IBlockTree blockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        IFileSystem fileSystem = new MockFileSystem();
        IDirectoryInfo tempDirectory = fileSystem.Directory.CreateTempSubdirectory();
        fileSystem.File.Create(Path.Join(tempDirectory.FullName, "abc-00000-00000000.era1"));
        EraImporter sut = new(fileSystem,
                              blockTree,
                              Substitute.For<IBlockValidator>(),
                              Substitute.For<IReceiptStorage>(),
                              Substitute.For<ISpecProvider>(),
                              LimboLogs.Instance,
                              "abc",
                              1);

        Assert.That(() => sut.ImportAsArchiveSync(tempDirectory.FullName, CancellationToken.None), Throws.TypeOf<EraImportException>());
        tempDirectory.Delete(recursive: true);
    }

    [Test]
    public async Task ImportAsArchiveSync_BlockCannotBeValidated_ThrowEraImportException()
    {
        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        (TmpDirectory tmpDirectory, IBlockTree sourceBlocktree) = await CreateEraFileSystem();
        using TmpDirectory _ = tmpDirectory;
        blockTree.SuggestBlock(sourceBlocktree.FindBlock(0)!, BlockTreeSuggestOptions.None);

        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        blockValidator.ValidateSuggestedBlock(Arg.Any<Block>(), out Arg.Any<string?>()).Returns(false);
        EraImporter sut = new(new FileSystem(),
                              blockTree,
                              blockValidator,
                              Substitute.For<IReceiptStorage>(),
                              Substitute.For<ISpecProvider>(),
                              LimboLogs.Instance,
                              "abc"
                              );

        Assert.That(() => sut.ImportAsArchiveSync(tmpDirectory.DirectoryPath, CancellationToken.None), Throws.TypeOf<EraImportException>());
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithExpected_DoesNotThrow()
    {
        const int ChainLength = 128;
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        const string NetworkName = "test";
        var fileSystem = new FileSystem();
        EraExporter exporter = new(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance, NetworkName);
        using var tmpDirectory = new TmpDirectory();
        string destinationPath = tmpDirectory.DirectoryPath;
        await exporter.Export(destinationPath!, 0, ChainLength - 1, 16);

        var eraStore = new EraStore(destinationPath, NetworkName, fileSystem);
        var accumulatorPath = Path.Combine(destinationPath, "something.txt");
        await eraStore.CreateAccumulatorFile(accumulatorPath, default);

        EraImporter sut = new(fileSystem, blockTree, Substitute.For<IBlockValidator>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance, NetworkName);

        Assert.DoesNotThrowAsync(() => sut.VerifyEraFiles(destinationPath, accumulatorPath));
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsithExpectedFromFileW_DoesNotThrow()
    {
        const int ChainLength = 128;
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        const string NetworkName = "test";
        var fileSystem = new FileSystem();
        EraExporter exporter = new(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance, NetworkName);

        using var tmpDirectory = new TmpDirectory();
        string destinationPath = tmpDirectory.DirectoryPath;
        await exporter.Export(destinationPath!, 0, ChainLength - 1, 16);

        var accumulatorPath = Path.Combine(destinationPath, EraExporter.AccumulatorFileName);

        EraImporter sut = new(fileSystem, blockTree, Substitute.For<IBlockValidator>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance, NetworkName);

        Assert.DoesNotThrowAsync(() => sut.VerifyEraFiles(destinationPath, accumulatorPath));
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithUnexpected_ThrowEraVerificationException()
    {
        const int ChainLength = 64;
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        const string NetworkName = "test";
        var fileSystem = new FileSystem();
        EraExporter exporter = new(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance, NetworkName);
        using var tmpDirectory = new TmpDirectory();
        var destinationPath = tmpDirectory.DirectoryPath;
        const int EpochSize = 16;
        await exporter.Export(destinationPath!, 0, ChainLength - 1, EpochSize);

        var accumulatorPath = Path.Combine(destinationPath, EraExporter.AccumulatorFileName);
        var accumulators = fileSystem.File.ReadAllLines(accumulatorPath).Select(s => Bytes.FromHexString(s)).ToArray();
        accumulators[accumulators.Length - 1] = new byte[32];
        await fileSystem.File.WriteAllLinesAsync(accumulatorPath, accumulators.Select(acc => acc.ToHexString()));

        EraImporter sut = new(fileSystem, blockTree, Substitute.For<IBlockValidator>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance, NetworkName);

        Assert.That(
            () => sut.VerifyEraFiles(destinationPath,  accumulatorPath),
            Throws.TypeOf<EraVerificationException>());
    }

    private async Task<(TmpDirectory, IBlockTree)> CreateEraFileSystem()
    {
        IBlockTree blockTree = Build.A.BlockTree().OfChainLength(512).TestObject;
        var dir = new TmpDirectory();
        var exporter = new EraExporter(new FileSystem(), blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance, "abc");
        await exporter.Export(dir.DirectoryPath, 0, 511, 16);
        return (dir, blockTree);
    }

}
