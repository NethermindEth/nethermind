// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Synchronization;
using NSubstitute;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace Nethermind.Era1.Test;
public class EraImporterTest
{
    [Test]
    public void ImportAsArchiveSync_DirectoryContainsNoEraFiles_ThrowEraImportException()
    {
        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        IFileSystem fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory("C:/test");
        EraImporter sut = new(fileSystem,
                              blockTree,
                              Substitute.For<IBlockValidator>(),
                              Substitute.For<IReceiptStorage>(),
                              Substitute.For<ISpecProvider>(),
                              "abc");

        Assert.That(() => sut.ImportAsArchiveSync("test", CancellationToken.None), Throws.TypeOf<EraImportException>());
    }

    [Test]
    public void ImportAsArchiveSync_DirectoryContainsWrongEraFiles_ThrowEraImportException()
    {
        IBlockTree blockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        IFileSystem fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory("C:/test");
        fileSystem.File.Create("C:/test/abc-00000-00000000.era1");
        EraImporter sut = new(fileSystem,
                              blockTree,
                              Substitute.For<IBlockValidator>(),
                              Substitute.For<IReceiptStorage>(),
                              Substitute.For<ISpecProvider>(),
                              "abc",
                              1);

        Assert.That(() => sut.ImportAsArchiveSync("test", CancellationToken.None), Throws.TypeOf<EraImportException>());
    }

    [Test]
    public async Task ImportAsArchiveSync_BlockCannotBeValidated_ThrowEraImportException()
    {
        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        IFileSystem fileSystem = await CreateEraFileSystem();
        EraImporter sut = new(fileSystem,
                              blockTree,
                              Substitute.For<IBlockValidator>(),
                              Substitute.For<IReceiptStorage>(),
                              Substitute.For<ISpecProvider>(),
                              "abc");

        Assert.That(() => sut.ImportAsArchiveSync("test", CancellationToken.None), Throws.TypeOf<EraImportException>());
    }

    private async Task<IFileSystem> CreateEraFileSystem()
    {
        IFileSystem fileSystem = new MockFileSystem();
        IBlockTree blockTree = Build.A.BlockTree().OfChainLength(512).TestObject;

        var exporter = new EraExporter(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), "abc");
        await exporter.Export("test", 0, 511, 16);
        return fileSystem;
    }

}
