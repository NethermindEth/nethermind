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
                              "abc",
                              1);

        Assert.That(() => sut.ImportAsArchiveSync(tempDirectory.FullName, CancellationToken.None), Throws.TypeOf<EraImportException>());
        tempDirectory.Delete(recursive: true);
    }

    [Test]
    public async Task ImportAsArchiveSync_BlockCannotBeValidated_ThrowEraImportException()
    {
        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        (IFileSystem fileSystem, IBlockTree sourceBlocktree) = await CreateEraFileSystem();
        blockTree.SuggestBlock(sourceBlocktree.FindBlock(0)!, BlockTreeSuggestOptions.None);

        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        blockValidator.ValidateSuggestedBlock(Arg.Any<Block>(), out Arg.Any<string?>()).Returns(false);
        EraImporter sut = new(fileSystem,
                              blockTree,
                              blockValidator,
                              Substitute.For<IReceiptStorage>(),
                              Substitute.For<ISpecProvider>(),
                              "abc");

        Assert.That(() => sut.ImportAsArchiveSync("test", CancellationToken.None), Throws.TypeOf<EraImportException>());
    }


    [Test]
    public void VerifyEraFiles_FilesAndAccumulatorsAreDifferentLength_ThrowArgumentException()
    {
        EraImporter sut = new(Substitute.For<IFileSystem>(), Substitute.For<IBlockTree>(), Substitute.For<IBlockValidator>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), "abc");

        Assert.That(() => sut.VerifyEraFiles(["abc", "abc"], [[0x0]]), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void VerifyEraFiles_AccumulatorsHaveInvalidSize_ThrowArgumentException()
    {
        EraImporter sut = new(Substitute.For<IFileSystem>(), Substitute.For<IBlockTree>(), Substitute.For<IBlockValidator>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), "abc");

        Assert.That(() => sut.VerifyEraFiles(["abc"], [[0x0]]), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithExpected_DoesNotThrow()
    {
        const int ChainLength = 128;
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        const string NetworkName = "test";
        var fileSystem = new MockFileSystem();
        EraExporter exporter = new(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), NetworkName);
        string destinationPath = "abc";
        await exporter.Export(destinationPath!, 0, ChainLength - 1, 16);
        var accumulators = new List<byte[]>();
        var eraFiles = EraReader.GetAllEraFiles(destinationPath, NetworkName, fileSystem).ToArray();
        foreach (var file in eraFiles)
        {
            using var reader = await EraReader.Create(fileSystem.File.OpenRead(file));
            accumulators.Add(await reader.ReadAccumulator());
        }

        EraImporter sut = new(fileSystem, blockTree, Substitute.For<IBlockValidator>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), NetworkName);

        Assert.DoesNotThrowAsync(() => sut.VerifyEraFiles(eraFiles, accumulators.ToArray()));
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsithExpectedFromFileW_DoesNotThrow()
    {
        const int ChainLength = 128;
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        const string NetworkName = "test";
        var fileSystem = new MockFileSystem();
        EraExporter exporter = new(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), NetworkName);
        string destinationPath = "abc";
        await exporter.Export(destinationPath!, 0, ChainLength - 1, 16);
        var accumulators = fileSystem.File.ReadAllLines(Path.Combine(destinationPath, EraExporter.AccumulatorFileName)).Select(s => Bytes.FromHexString(s)).ToArray();

        EraImporter sut = new(fileSystem, blockTree, Substitute.For<IBlockValidator>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), NetworkName);

        Assert.DoesNotThrowAsync(() => sut.VerifyEraFiles(EraReader.GetAllEraFiles(destinationPath, NetworkName, fileSystem).ToArray(), accumulators.ToArray()));
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithUnexpected_ThrowEraVerificationException()
    {
        const int ChainLength = 64;
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        const string NetworkName = "test";
        var fileSystem = new MockFileSystem();
        EraExporter exporter = new(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), NetworkName);
        var destinationPath = "abc";
        const int EpochSize = 16;
        await exporter.Export(destinationPath!, 0, ChainLength - 1, EpochSize);
        var accumulators = fileSystem.File.ReadAllLines(Path.Combine(destinationPath, EraExporter.AccumulatorFileName)).Select(s => Bytes.FromHexString(s)).ToArray();
        accumulators[accumulators.Length - 1] = new byte[32];

        EraImporter sut = new(fileSystem, blockTree, Substitute.For<IBlockValidator>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), NetworkName);

        Assert.That(
            () => sut.VerifyEraFiles(EraReader.GetAllEraFiles(destinationPath, NetworkName, fileSystem).ToArray(), accumulators.ToArray()),
            Throws.TypeOf<EraVerificationException>());
    }

    private async Task<(IFileSystem, IBlockTree)> CreateEraFileSystem()
    {
        IFileSystem fileSystem = new MockFileSystem();
        IBlockTree blockTree = Build.A.BlockTree().OfChainLength(512).TestObject;

        var exporter = new EraExporter(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), "abc");
        await exporter.Export("test", 0, 511, 16);
        return (fileSystem, blockTree);
    }

}
