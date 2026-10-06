// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Security.Cryptography;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Era1.Exceptions;
using Testably.Abstractions;

namespace Nethermind.Era1.Test;

public class EraManifestGeneratorTests
{
    private static readonly IFileSystem FileSystem = new RealFileSystem();
    private const string MainnetNetwork = "mainnet";
    private const string MainnetTestDataPath = "testdata/mainnet";

    [Test]
    public async Task GenerateAsync_MissingManifestsBesideOtherNetworkFiles_RegeneratesImportableExporterOutput()
    {
        await using IContainer container = await EraTestModule.CreateExportedEraEnv(64);
        string directory = container.ResolveTempDirPath();
        string accumulatorsPath = Path.Combine(directory, EraExporter.AccumulatorFileName);
        string checksumsPath = Path.Combine(directory, EraExporter.ChecksumsFileName);
        byte[] exportedAccumulators = await File.ReadAllBytesAsync(accumulatorsPath);
        byte[] exportedChecksums = await File.ReadAllBytesAsync(checksumsPath);
        File.Delete(accumulatorsPath);
        File.Delete(checksumsPath);
        foreach (string mainnetFile in Directory.GetFiles(MainnetTestDataPath, "*.era1"))
        {
            File.Copy(mainnetFile, Path.Combine(directory, Path.GetFileName(mainnetFile)));
        }

        await EraManifestGenerator.GenerateAsync(directory, EraTestModule.TestNetwork, directory, FileSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllBytesAsync(accumulatorsPath), Is.EqualTo(exportedAccumulators));
            Assert.That(await File.ReadAllBytesAsync(checksumsPath), Is.EqualTo(exportedChecksums));
        }

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(container.Resolve<IBlockTree>().FindBlock(0, BlockTreeLookupOptions.None)!).TestObject;
        await using IContainer importContainer = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .Build();
        await importContainer.Resolve<IEraImporter>().Import(directory, 0, ulong.MaxValue, accumulatorsPath);
        Assert.That(inTree.BestKnownNumber, Is.EqualTo(63));
    }

    [Test]
    public async Task GenerateAsync_MainnetFiles_WritesRootAndSha256PerFile()
    {
        using TempPath output = TempPath.GetTempDirectory();

        await EraManifestGenerator.GenerateAsync(MainnetTestDataPath, MainnetNetwork, output.Path, FileSystem);

        string[] eraFiles = Directory.GetFiles(MainnetTestDataPath, "*.era1").Order(StringComparer.Ordinal).ToArray();
        string[] accumulators = await File.ReadAllLinesAsync(Path.Combine(output.Path, EraExporter.AccumulatorFileName));
        string[] checksums = await File.ReadAllLinesAsync(Path.Combine(output.Path, EraExporter.ChecksumsFileName));
        Assert.That(accumulators, Has.Length.EqualTo(eraFiles.Length));
        Assert.That(checksums, Has.Length.EqualTo(eraFiles.Length));

        for (int i = 0; i < eraFiles.Length; i++)
        {
            string fileName = Path.GetFileName(eraFiles[i]);
            string rootPrefix = fileName.Split('-')[2][..8];
            string sha256 = SHA256.HashData(await File.ReadAllBytesAsync(eraFiles[i])).ToHexString(true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accumulators[i], Does.StartWith($"0x{rootPrefix}"));
                Assert.That(accumulators[i], Does.EndWith($" {fileName}"));
                Assert.That(checksums[i], Is.EqualTo($"{sha256} {fileName}"));
            }
        }
    }

    [Test]
    public async Task GenerateAsync_SingleFile_WritesOneLineManifest()
    {
        await using IContainer container = await EraTestModule.CreateExportedEraEnv(64);
        string directory = container.ResolveTempDirPath();
        string[] exportedAccumulators = await File.ReadAllLinesAsync(Path.Combine(directory, EraExporter.AccumulatorFileName));
        string eraFile = Path.Combine(directory, exportedAccumulators[1].Split(' ')[1]);
        using TempPath output = TempPath.GetTempDirectory();

        await EraManifestGenerator.GenerateAsync(eraFile, EraTestModule.TestNetwork, output.Path, FileSystem);

        Assert.That(await File.ReadAllLinesAsync(Path.Combine(output.Path, EraExporter.AccumulatorFileName)), Is.EqualTo(new[] { exportedAccumulators[1] }));
    }

    [Test]
    public async Task GenerateAsync_StoredAccumulatorDoesNotMatchContent_Throws()
    {
        await using IContainer container = await EraTestModule.CreateExportedEraEnv(32);
        string directory = container.ResolveTempDirPath();
        string eraFile = Directory.GetFiles(directory, "*.era1")[0];
        long accumulatorValueOffset;
        using (E2StoreReader reader = new(eraFile))
        {
            accumulatorValueOffset = reader.AccumulatorOffset + 8;
        }

        await using (FileStream stream = new(eraFile, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = accumulatorValueOffset;
            int value = stream.ReadByte();
            stream.Position = accumulatorValueOffset;
            stream.WriteByte((byte)(value ^ 0xff));
        }

        using TempPath output = TempPath.GetTempDirectory();
        Assert.That(() => EraManifestGenerator.GenerateAsync(directory, EraTestModule.TestNetwork, output.Path, FileSystem), Throws.TypeOf<EraVerificationException>());
    }

    public enum FileSetDefect
    {
        TruncatedTemporaryFile,
        UnrenamedTemporaryFile,
        TemporaryFileBesideFinishedFile,
        MissingEpoch,
    }

    [TestCase(FileSetDefect.TruncatedTemporaryFile, "Unable to read abc-00004-00000000.era1")]
    [TestCase(FileSetDefect.UnrenamedTemporaryFile, "abc-00003-00000000.era1 does not match its accumulator")]
    [TestCase(FileSetDefect.TemporaryFileBesideFinishedFile, "Epoch 2 has more than one era1 file")]
    [TestCase(FileSetDefect.MissingEpoch, "Epoch 1 is missing")]
    public async Task GenerateAsync_InvalidFileSet_ThrowsNamingTheDefect(FileSetDefect defect, string expectedMessage)
    {
        await using IContainer container = await EraTestModule.CreateExportedEraEnv(64);
        string directory = container.ResolveTempDirPath();
        string EpochFile(int epoch) => Directory.GetFiles(directory, $"{EraTestModule.TestNetwork}-{epoch:D5}-*.era1").Single();
        string TemporaryFile(int epoch) => Path.Combine(directory, $"{EraTestModule.TestNetwork}-{epoch:D5}-00000000.era1");

        switch (defect)
        {
            case FileSetDefect.TruncatedTemporaryFile:
                await File.WriteAllBytesAsync(TemporaryFile(4), (await File.ReadAllBytesAsync(EpochFile(3)))[..100]);
                break;
            case FileSetDefect.UnrenamedTemporaryFile:
                File.Move(EpochFile(3), TemporaryFile(3));
                break;
            case FileSetDefect.TemporaryFileBesideFinishedFile:
                File.Copy(EpochFile(2), TemporaryFile(2));
                break;
            case FileSetDefect.MissingEpoch:
                File.Delete(EpochFile(1));
                break;
        }

        using TempPath output = TempPath.GetTempDirectory();
        Assert.That(() => EraManifestGenerator.GenerateAsync(directory, EraTestModule.TestNetwork, output.Path, FileSystem),
            Throws.InstanceOf<EraException>().With.Message.Contains(expectedMessage));
    }

    public enum ManifestEdit
    {
        None,
        AlteredAccumulator,
        AlteredChecksum,
        ReorderedChecksums,
        DuplicatedChecksum,
        ExtraChecksum,
        MissingChecksum,
        MalformedChecksum,
    }

    [Test]
    public async Task VerifyAsync_EditedManifest_ReportsMismatch([Values] ManifestEdit edit)
    {
        await using IContainer container = await EraTestModule.CreateExportedEraEnv(64);
        string directory = container.ResolveTempDirPath();
        string manifestPath = Path.Combine(directory, edit == ManifestEdit.AlteredAccumulator ? EraExporter.AccumulatorFileName : EraExporter.ChecksumsFileName);
        List<string> lines = [.. await File.ReadAllLinesAsync(manifestPath)];
        switch (edit)
        {
            case ManifestEdit.AlteredAccumulator:
            case ManifestEdit.AlteredChecksum:
                lines[2] = $"0x{new string('0', 64)} {lines[2].Split(' ')[1]}";
                break;
            case ManifestEdit.ReorderedChecksums:
                (lines[1], lines[2]) = (lines[2], lines[1]);
                break;
            case ManifestEdit.DuplicatedChecksum:
                lines.Insert(1, lines[1]);
                break;
            case ManifestEdit.ExtraChecksum:
                lines.Add($"0x{new string('0', 64)} {EraTestModule.TestNetwork}-00004-00000000.era1");
                break;
            case ManifestEdit.MissingChecksum:
                lines.RemoveAt(lines.Count - 1);
                break;
            case ManifestEdit.MalformedChecksum:
                lines[2] = "not-a-hash";
                break;
        }

        await File.WriteAllLinesAsync(manifestPath, lines);

        IReadOnlyList<string> mismatches = await EraManifestGenerator.VerifyAsync(directory, EraTestModule.TestNetwork, FileSystem);

        Assert.That(mismatches, edit == ManifestEdit.None ? Is.Empty : Is.Not.Empty);
    }

    public enum ManifestFormat
    {
        LegacyFileNames,
        HashOnly,
        HashWithoutPrefix,
    }

    [Test]
    public async Task VerifyAsync_ImporterCompatibleManifest_AcceptsHashes([Values] ManifestFormat format)
    {
        await using IContainer container = await EraTestModule.CreateExportedEraEnv(64);
        string directory = container.ResolveTempDirPath();
        foreach (string manifestFile in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName })
        {
            string path = Path.Combine(directory, manifestFile);
            string[] lines = await File.ReadAllLinesAsync(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string hash = lines[i].Split(' ')[0];
                lines[i] = format switch
                {
                    ManifestFormat.LegacyFileNames => $"{hash} {EraTestModule.TestNetwork}-{i:D5}-00000000.era1",
                    ManifestFormat.HashOnly => hash,
                    ManifestFormat.HashWithoutPrefix => hash[2..],
                    _ => throw new ArgumentOutOfRangeException(nameof(format)),
                };
            }
            await File.WriteAllLinesAsync(path, lines);
        }

        Assert.That(await EraManifestGenerator.VerifyAsync(directory, EraTestModule.TestNetwork, FileSystem), Is.Empty);
    }
}
