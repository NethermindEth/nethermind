// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Era1.Exceptions;
using NSubstitute;
using Testably.Abstractions;

namespace Nethermind.Era1.Test;

public class EraStoreTests
{
    [TestCase(1000, 16UL, 32UL)]
    [TestCase(1000, 16UL, 64UL)]
    [TestCase(1000, 50UL, 100UL)]
    public async Task SmallestAndLowestBlock_ShouldBeCorrect(int chainLength, ulong start, ulong end)
    {
        await using IContainer ctx = await EraTestModule.CreateExportedEraEnv(chainLength, start, end);
        string tmpDirectory = ctx.ResolveTempDirPath();

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(eraStore.FirstBlock, Is.EqualTo(start));
            Assert.That(eraStore.LastBlock, Is.EqualTo(end));
        }
    }

    public enum ChecksumManifest
    {
        Exported,
        HashOnly,
        Reversed,
        FirstEraFileRemoved,
        HashOnlyFirstEraFileRemoved,
    }

    [Test]
    public async Task FindBlockAndReceipts_ChecksumManifestLayout_VerifiesEveryEpoch([Values] ChecksumManifest manifest)
    {
        const ulong eraSize = 16;
        await using IContainer ctx = await EraTestModule.CreateExportedEraEnv(64);
        string directory = ctx.ResolveTempDirPath();
        string checksumsPath = Path.Combine(directory, EraExporter.ChecksumsFileName);
        string[] lines = await File.ReadAllLinesAsync(checksumsPath);
        ulong firstBlock = 0;
        switch (manifest)
        {
            case ChecksumManifest.HashOnly:
                lines = [.. lines.Select(static line => line.Split(' ')[0])];
                break;
            case ChecksumManifest.Reversed:
                Array.Reverse(lines);
                break;
            case ChecksumManifest.FirstEraFileRemoved:
                File.Delete(Path.Combine(directory, lines[0].Split(' ')[1]));
                firstBlock = eraSize;
                break;
            case ChecksumManifest.HashOnlyFirstEraFileRemoved:
                File.Delete(Path.Combine(directory, lines[0].Split(' ')[1]));
                lines = [.. lines.Skip(1).Select(static line => line.Split(' ')[0])];
                firstBlock = eraSize;
                break;
        }
        await File.WriteAllLinesAsync(checksumsPath, lines);

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(directory, null);

        using (Assert.EnterMultipleScope())
        {
            for (ulong block = firstBlock; block < 64; block += eraSize)
            {
                Assert.That(async () => await eraStore.FindBlockAndReceipts(block), Throws.Nothing, $"block {block}");
            }
        }
    }

    public enum DuplicateManifest
    {
        NamesOneFile,
        NamesOneOfThree,
        NamesOneInReversedLines,
        NamesOneOfTwoCaseVariants,
        PlaceholderNamesFromARelease,
        NamesAbsentFile,
        HashOnly,
        HashOnlyIdenticalCopies,
        NoEntry,
    }

    [Test]
    public async Task Create_WhenEpochHasTwoFiles_ReadsTheFileTheManifestNames([Values] DuplicateManifest manifest, [Values] bool reversed)
    {
        await using IContainer ctx = await EraTestModule.CreateExportedEraEnv(64);
        string directory = ctx.ResolveTempDirPath();
        string EpochFile(int epoch) => Directory.GetFiles(directory, $"{EraTestModule.TestNetwork}-{epoch:D5}-*.era1").Single();
        string named = Path.GetFileName(EpochFile(2));
        // A stale file of epoch 2 that holds epoch 1's blocks, so reading it would fail: a placeholder, a finished name, or
        // the named file's name in another case. Its name sorts before the named file's.
        string hash = named.Split('-')[2][..8];
        string stale = manifest switch
        {
            DuplicateManifest.NamesOneOfTwoCaseVariants => named.Replace(hash, hash.ToUpperInvariant()),
            DuplicateManifest.NamesAbsentFile or DuplicateManifest.HashOnly or DuplicateManifest.HashOnlyIdenticalCopies or DuplicateManifest.NoEntry
                => $"{EraTestModule.TestNetwork}-00002-0000000a.era1",
            _ => $"{EraTestModule.TestNetwork}-00002-00000000.era1",
        };
        Assume.That(File.Exists(Path.Combine(directory, stale)), Is.False, "needs a case-sensitive file system");
        File.Copy(EpochFile(manifest == DuplicateManifest.HashOnlyIdenticalCopies ? 2 : 1), Path.Combine(directory, stale));
        if (manifest == DuplicateManifest.NamesOneOfThree)
            File.Copy(EpochFile(1), Path.Combine(directory, $"{EraTestModule.TestNetwork}-00002-00000001.era1"));
        string checksumsPath = Path.Combine(directory, EraExporter.ChecksumsFileName);
        string[] lines = await File.ReadAllLinesAsync(checksumsPath);
        lines = manifest switch
        {
            DuplicateManifest.NamesOneInReversedLines => [.. lines.AsEnumerable().Reverse()],
            // Releases up to 2.2.2 listed the placeholder name instead of the file it was renamed to.
            DuplicateManifest.PlaceholderNamesFromARelease => [.. lines.Select(static line => line[..^"00000000.era1".Length] + "00000000.era1")],
            DuplicateManifest.NamesAbsentFile => [.. lines.Select(line => line.Replace(named, $"{EraTestModule.TestNetwork}-00002-ffffffff.era1"))],
            DuplicateManifest.HashOnly or DuplicateManifest.HashOnlyIdenticalCopies => [.. lines.Select(static line => line.Split(' ')[0])],
            DuplicateManifest.NoEntry => [.. lines.Where(static line => !line.Contains("-00002-"))],
            _ => lines,
        };
        await File.WriteAllLinesAsync(checksumsPath, lines);

        // List the files in ordinal order or in reverse, so neither the first nor the last file can pass for the named one.
        IFileSystem real = new RealFileSystem();
        IDirectory listing = Substitute.For<IDirectory>();
        listing.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EnumerationOptions>()).Returns(c =>
        {
            string[] files = [.. real.Directory.GetFiles(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<EnumerationOptions>(2)).Order(StringComparer.Ordinal)];
            return reversed ? [.. files.AsEnumerable().Reverse()] : files;
        });
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(real.File);
        fileSystem.Directory.Returns(listing);
        EraStore Create() => new(ctx.Resolve<ISpecProvider>(), ctx.Resolve<IBlockValidator>(), fileSystem, EraTestModule.TestNetwork, 16, null, directory);

        // Without a usable name, the store takes the only finished file, else the only one whose SHA-256 is listed.
        if (manifest is DuplicateManifest.HashOnlyIdenticalCopies or DuplicateManifest.NoEntry)
        {
            Assert.That(() => Create(), Throws.TypeOf<EraException>().With.Message.Contains("Epoch 2").And.Message.Contains(named).And.Message.Contains(stale));
            return;
        }

        using EraStore eraStore = Create();
        using (Assert.EnterMultipleScope())
        {
            for (ulong block = 0; block < 64; block += 8)
                Assert.That(async () => await eraStore.FindBlockAndReceipts(block), Throws.Nothing, $"block {block}");
        }
    }

    [Test]
    public async Task FindBlockAndReceipts_EpochWithoutChecksumEntry_ThrowsEraVerificationException()
    {
        await using IContainer ctx = await EraTestModule.CreateExportedEraEnv(64);
        string directory = ctx.ResolveTempDirPath();
        string checksumsPath = Path.Combine(directory, EraExporter.ChecksumsFileName);
        string[] lines = await File.ReadAllLinesAsync(checksumsPath);
        await File.WriteAllLinesAsync(checksumsPath, lines.Where(static (_, i) => i != 1));

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(directory, null);

        Assert.That(async () => await eraStore.FindBlockAndReceipts(16),
            Throws.TypeOf<EraVerificationException>().With.Message.Contains("epoch 1"));
    }
}
