// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Nethermind.EraE.Config;
using Nethermind.EraE.E2Store;
using EraException = Nethermind.Era1.Exceptions.EraException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Store;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.EraE.Test.Export;

public class EraExporterTests
{
    [TestCase(1, 0UL, 0UL, 1UL, 1)]
    [TestCase(3, 0UL, 2UL, 1UL, 3)]
    [TestCase(16, 0UL, 15UL, 16UL, 1)]
    [TestCase(32, 0UL, 31UL, 16UL, 2)]
    [TestCase(48, 8UL, 39UL, 16UL, 3)]
    public async Task Export_WithVaryingChainLength_CreatesCorrectNumberOfEpochFiles(
        int chainLength, ulong from, ulong to, ulong eraSize, int expectedEraFiles)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .AddSingleton<IEraEConfig>(new EraEConfig
            {
                MaxEraSize = eraSize,
                NetworkName = EraETestModule.TestNetwork
            })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, from, to);

        string[] allFiles = System.IO.Directory.GetFiles(tmpDirectory);
        string[] eraFiles = allFiles.Where(f => f.EndsWith(EraPathUtils.FileExtension)).ToArray();
        Assert.That(eraFiles.Length, Is.EqualTo(expectedEraFiles));
    }

    [Test]
    public async Task Export_WhenLegacyEraeFileExists_SkipsEpochWithoutWritingEre()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, 0, 15);

        string ereFile = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}").Single();
        string eraeFile = System.IO.Path.ChangeExtension(ereFile, EraPathUtils.LegacyFileExtension);
        System.IO.File.Move(ereFile, eraeFile);

        await sut.Export(tmpDirectory, 0, 15);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(System.IO.File.Exists(eraeFile), Is.True, "pre-existing legacy .erae file must be kept on re-export");
            Assert.That(System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}"), Is.Empty,
                "epoch already present as .erae must be skipped, not re-exported as a duplicate .ere");
        }
    }

    [TestCase(0UL, 31UL, 32UL, 63UL, false)]
    [TestCase(32UL, 63UL, 0UL, 31UL, false)]
    [TestCase(0UL, 47UL, 16UL, 63UL, false)]
    [TestCase(0UL, 31UL, 32UL, 63UL, true)]
    [TestCase(0UL, 40UL, 0UL, 63UL, false)]
    [TestCase(8UL, 15UL, 0UL, 15UL, false)]
    public async Task Export_IntoDirectoryWithEarlierExport_WritesManifestsOfWholeDirectory(
        ulong firstFrom, ulong firstTo, ulong secondFrom, ulong secondTo, bool removeFirstEraFile)
    {
        const ulong eraSize = 16;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(64).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, firstFrom, firstTo);
        ulong from = Math.Min(firstFrom, secondFrom);
        if (removeFirstEraFile)
        {
            File.Delete(Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Order().First());
            from += eraSize;
        }

        await sut.Export(directory, secondFrom, secondTo);

        ulong to = Math.Max(firstTo, secondTo);
        using TempPath reference = TempPath.GetTempDirectory();
        await sut.Export(reference.Path, from, to);

        using (Assert.EnterMultipleScope())
        {
            foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsSHA256FileName, EraExporter.ChecksumsFileName })
            {
                Assert.That(await File.ReadAllLinesAsync(Path.Combine(directory, manifest)),
                    Is.EqualTo(await File.ReadAllLinesAsync(Path.Combine(reference.Path, manifest))), manifest);
            }

            Assert.That(Directory.GetFiles(directory).Count(EraPathUtils.IsEraFile), Is.EqualTo((int)((to - from) / eraSize + 1)), "one file per epoch");
            await AssertReadsBack(container, directory, from, to);
        }
    }

    [TestCase(false, 0UL, 0UL, 15UL)]
    [TestCase(false, 0UL, 4UL, 12UL)]
    [TestCase(true, 1UL, 1UL, 15UL)]
    public async Task Export_WhenFileCoversRequestedBlocks_ReusesIt(bool postMerge, ulong firstFrom, ulong from, ulong to)
    {
        await using IContainer container = (postMerge
            ? EraETestModule.BuildContainerBuilderWithPostMergeBlockTreeOfLength(16)
            : EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16)).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, firstFrom, 15);
        string[] files = Directory.GetFiles(directory);
        string[] checksums = await File.ReadAllLinesAsync(Path.Combine(directory, EraExporter.ChecksumsSHA256FileName));
        DropReceipts(container, firstFrom, 15);

        await sut.Export(directory, from, to);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(directory), Is.EquivalentTo(files));
            Assert.That(await File.ReadAllLinesAsync(Path.Combine(directory, EraExporter.ChecksumsSHA256FileName)), Is.EqualTo(checksums));
        }
    }

    [TestCase("-noproofs.ere")]
    [TestCase(".erae")]
    public async Task Export_WhenEpochHasFinishedPlaceholder_RewritesEpoch(string suffix)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 15);
        string finalName = Path.GetFileName(Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Single());

        // Finished and listed by the manifests: only its name tells that the export never published it.
        string placeholderName = $"{EraETestModule.TestNetwork}-00000-00000000{suffix}";
        File.Move(Path.Combine(directory, finalName), Path.Combine(directory, placeholderName));
        foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsSHA256FileName })
        {
            string path = Path.Combine(directory, manifest);
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace(finalName, placeholderName));
        }

        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory).Where(EraPathUtils.IsEraFile).Select(Path.GetFileName), Is.EqualTo(new[] { finalName }));
        await AssertReadsBack(container, directory, 0, 15);
    }

    [Test]
    public async Task Export_WhenWiderLegacyFileExists_ReusesItAndDeletesOnlyFilesItSupersedes()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        using TempPath partial = TempPath.GetTempDirectory();
        await sut.Export(partial.Path, 0, 8);
        await sut.Export(directory, 0, 15);
        string full = Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Single();
        string legacy = Path.ChangeExtension(full, EraPathUtils.LegacyFileExtension);
        File.Move(full, legacy);

        string partialFile = Directory.GetFiles(partial.Path, $"*{EraPathUtils.FileExtension}").Single();
        File.Copy(partialFile, Path.Combine(directory, Path.GetFileName(partialFile)));
        await File.WriteAllTextAsync(Path.Combine(directory, EraPathUtils.Filename(EraETestModule.TestNetwork, 0, Keccak.Zero)), "interrupted");
        string hash = Path.GetFileName(partialFile).Split('-')[2];
        string[] unrelated = [$"xyz-00000-{hash}-noproofs.ere", $"abc-00001-{hash}-noproofs.ere", $"abc-00000-{hash}.era1", $"abc-00000-{hash}.txt", $"abc-00000-{hash}-noproofs.ere.bak"];
        foreach (string name in unrelated) File.Copy(partialFile, Path.Combine(directory, name));
        DropReceipts(container, 0, 15);

        await sut.Export(directory, 0, 8);

        Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName), Is.EquivalentTo(unrelated.Append(Path.GetFileName(legacy))
            .Concat([EraExporter.AccumulatorFileName, EraExporter.ChecksumsSHA256FileName, EraExporter.ChecksumsFileName])));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Export_WhenEarlierFileWouldLoseBlocksOrIsUnreadable_ThrowsAndChangesNothing(bool unreadable)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 32, 40);
        if (unreadable) await File.WriteAllTextAsync(Path.Combine(directory, "abc-00002-12345678-noproofs.ere"), "not an era file");
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);

        Assert.That(() => sut.Export(directory, unreadable ? 32UL : 40UL, 47),
            Throws.TypeOf<EraException>().With.Message.Contains(unreadable ? "12345678" : "(blocks 32-40)"));
        Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [Test]
    public async Task Export_OnCaseInsensitiveFileSystem_KeepsTheSurvivorOfACaseOnlyRename()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).AddSingleton(CaseInsensitiveFileSystem()).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 8, 15);
        string file = Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Single();
        string hash = Path.GetFileName(file).Split('-')[2];
        File.Move(file, Path.Combine(directory, Path.GetFileName(file).Replace(hash, hash.ToUpperInvariant())));

        // The new file replaces the upper-case one, which the file system no longer lists as a second name.
        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory).Count(EraPathUtils.IsEraFile), Is.EqualTo(1));
        await AssertReadsBack(container, directory, 0, 15);
    }

    [Test]
    public async Task Export_WhenEreAndLegacyFileCoverTheEpoch_KeepsTheEre()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 15);
        string ere = Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Single();
        File.Copy(ere, Path.ChangeExtension(ere, EraPathUtils.LegacyFileExtension));
        DropReceipts(container, 0, 15);

        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory).Where(EraPathUtils.IsEraFile), Is.EqualTo(new[] { ere }));
    }

    [TestCase(".ere", false, true)]
    [TestCase(".erae", false, true)]
    [TestCase("-noreceipts.ere", false, false)]
    [TestCase("-noproofs-noreceipts.ere", false, false)]
    [TestCase(".ere", true, false)]
    public async Task Export_WhenEpochHasAFileOfAnotherProfile_RanksAndKeepsIt(string otherSuffix, bool rewrite, bool otherIsReused)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        using TempPath source = TempPath.GetTempDirectory();
        await sut.Export(rewrite ? source.Path : directory, rewrite ? 8UL : 0UL, 15);
        string ours = Directory.GetFiles(rewrite ? source.Path : directory, $"*{EraPathUtils.FileExtension}").Single();
        string other = Path.Combine(directory, Path.GetFileName(ours).Replace($"-{EraPathUtils.NoProofsProfile}{EraPathUtils.FileExtension}", otherSuffix));
        Directory.CreateDirectory(directory);
        File.Copy(ours, other);
        if (!rewrite) DropReceipts(container, 0, 15);

        // Equal ranges: a file with receipts, then with proofs, then .ere is reused. Rewrite: the new file must not delete it.
        await sut.Export(directory, 0, 15);

        string[] checksums = await File.ReadAllLinesAsync(Path.Combine(directory, EraExporter.ChecksumsSHA256FileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(directory).Where(EraPathUtils.IsEraFile).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { Path.GetFileName(other), Path.GetFileName(ours) }), "no file of another profile is deleted");
            Assert.That(checksums.Single(), Does.EndWith("  " + Path.GetFileName(otherIsReused ? other : ours)), "the manifest names the file that holds the epoch");
        }

        if (rewrite) await AssertReadsBack(container, directory, 0, 15);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Export_WhenSiblingDiffersOnlyInHashCase_KeepsOneFile(bool rewrite)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, rewrite ? 8UL : 0UL, 15);
        RenameToUpperCaseHash(Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Single(), keepOriginal: !rewrite);

        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory).Count(EraPathUtils.IsEraFile), Is.EqualTo(1));
        await AssertReadsBack(container, directory, 0, 15);
    }

    [Test]
    public async Task Export_WhenMoveFails_KeepsEarlierFile()
    {
        bool failMoves = false;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48)
            .AddSingleton(CreateFaultyFileSystem(_ => failMoves, static _ => false)).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 32, 40);
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);
        failMoves = true;

        Assert.That(() => sut.Export(directory, 32, 47), Throws.TypeOf<IOException>());
        Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [TestCase(0UL, 20UL)]
    [TestCase(8UL, 15UL)]
    public async Task Export_WhenLastEpochWouldLoseBlocks_ChangesNothing(ulong firstFrom, ulong firstTo)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, firstFrom, firstTo);
        await sut.Export(directory, 36, 47);
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);

        // The earlier epochs could be written (from 8, epoch 0 under the name of its earlier file), but epoch 2 can neither
        // reuse 36-47 nor replace it without losing 41-47.
        Assert.That(() => sut.Export(directory, 0, 40), Throws.TypeOf<EraException>().With.Message.Contains("(blocks 36-47)"));
        Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Export_WhenFailedReexportLeavesTwoFiles_StoreReadsTheNamedOneUntilARerun(bool reversed)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork, Concurrency = 1 }).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 20);
        DropReceipts(container, 32, 40);

        // Epoch 1 is rewritten as 16-31 under a new name, then epoch 2 fails: the manifests still name the old file.
        Assert.That(() => sut.Export(directory, 0, 40), Throws.TypeOf<EraException>());
        Assert.That(Directory.GetFiles(directory, $"{EraETestModule.TestNetwork}-00001-*"), Has.Length.EqualTo(2));
        using (IEraStore store = CreateStore(container, directory, reversed))
            await AssertReadsBack(container, store, 0, 20);

        await sut.Export(directory, 0, 31);

        Assert.That(Directory.GetFiles(directory, $"{EraETestModule.TestNetwork}-00001-*"), Has.Length.EqualTo(1));
        using (IEraStore store = CreateStore(container, directory, reversed))
            await AssertReadsBack(container, store, 0, 31);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Export_WhenExportFails_DeletesNoEarlierFile(bool failManifest)
    {
        bool fail = false;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48)
            .AddSingleton(CreateFaultyFileSystem(
                path => fail && !failManifest && Path.GetFileName(path).Contains("-00002-"),
                path => fail && failManifest && Path.GetFileName(path) == EraExporter.AccumulatorFileName)).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 20);
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);
        fail = true;

        // Epoch 1 is rewritten as 16-31 and supersedes its earlier file, then epoch 2 or the manifests fail.
        Assert.That(() => sut.Export(directory, 0, 40), Throws.TypeOf<IOException>());
        Assert.That(before.Keys.Where(File.Exists).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [Test]
    public async Task Export_WhenFailingAfterReplacingAFileUnderItsOwnName_DropsItsEntriesUntilARetry([Values] bool postMerge, [Values] bool failManifest)
    {
        // The genesis of the post-merge fixture is pre-merge, so its wider export starts at 1.
        ulong widerFrom = postMerge ? 1UL : 0UL;
        string epoch0 = "";
        int moves = 0;
        int faults = 0;
        bool armed = false;
        await using IContainer container = (postMerge
                ? EraETestModule.BuildContainerBuilderWithPostMergeBlockTreeOfLength(48)
                : EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48))
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork, Concurrency = 1 })
            .AddSingleton(CreateFaultyFileSystem(
                path =>
                {
                    if (path == epoch0) moves++;
                    return false;
                },
                path =>
                {
                    // Only after the move, so the manifests can still be rewritten before any epoch is written.
                    bool fail = armed && moves > 0 && Path.GetFileName(path) == EraExporter.AccumulatorFileName;
                    if (fail) faults++;
                    return fail;
                })).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        using TempPath reference = TempPath.GetTempDirectory();
        foreach ((string target, ulong first) in new[] { (directory, 8UL), (reference.Path, widerFrom) })
        {
            await sut.Export(target, first, 15);
            await sut.Export(target, 32, 47);
        }

        epoch0 = Directory.GetFiles(directory, $"{EraETestModule.TestNetwork}-00000-*").Single();
        string[] manifests = [EraExporter.AccumulatorFileName, EraExporter.ChecksumsSHA256FileName, EraExporter.ChecksumsFileName];
        // Rows are in epoch order, so all but the first belong to the control epoch 2.
        string[][] controlRows = [.. manifests.Select(m => File.ReadAllLines(Path.Combine(directory, m))[1..])];
        if (!failManifest) DropReceipts(container, 16, 31);
        armed = failManifest;

        // The wider epoch-0 file ends at the same block, so it replaces the earlier one under its name; then the manifests
        // or epoch 1 fail.
        Assert.That(() => sut.Export(directory, widerFrom, failManifest ? 15UL : 31UL),
            failManifest ? Throws.TypeOf<IOException>() : Throws.TypeOf<EraException>());
        armed = false;
        using (E2StoreReader replaced = new(epoch0))
            Assert.That(replaced.First, Is.EqualTo(widerFrom), "the failed export replaced the file");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(faults, Is.EqualTo(failManifest ? 1 : 0), "manifest faults");
            for (int i = 0; i < manifests.Length; i++)
                Assert.That(File.ReadAllLines(Path.Combine(directory, manifests[i])), Is.EqualTo(controlRows[i]), $"{manifests[i]} after the failure");
        }

        await sut.Export(directory, widerFrom, 15);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(moves, Is.EqualTo(1), "the retry reuses the replaced file");
            foreach (string manifest in manifests)
            {
                Assert.That(File.ReadAllLines(Path.Combine(directory, manifest)),
                    Is.EqualTo(File.ReadAllLines(Path.Combine(reference.Path, manifest))), $"{manifest} after the retry");
            }
        }

        await AssertReadsBack(container, directory, widerFrom, 15);
        await AssertReadsBack(container, directory, 32, 47);
    }

    [Test]
    public async Task Export_WhenDroppingTheEntriesOfAReplacedNameFails_WritesNoEpoch()
    {
        bool fail = false;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48)
            .AddSingleton(CreateFaultyFileSystem(static _ => false, path => fail && Path.GetFileName(path) == EraExporter.AccumulatorFileName)).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 8, 20);
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);
        fail = true;

        // Epoch 0 would replace its file under the same name, so its entries are dropped first, and that fails.
        Assert.That(() => sut.Export(directory, 0, 40), Throws.TypeOf<IOException>());
        Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));

        fail = false;
        await sut.Export(directory, 0, 40);
        await AssertReadsBack(container, directory, 0, 40);
    }

    [Test]
    public async Task Export_WhenCalled_CreatesMetadataFile([Values("checksums_sha256.txt", "checksums.txt", "accumulators.txt")] string fileName)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        Assert.That(System.IO.File.Exists(System.IO.Path.Combine(tmpDirectory, fileName)), Is.True);
    }

    [Test]
    public async Task Export_WhenCalled_ChecksumsFileHasCorrectFormat()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        string[] lines = await System.IO.File.ReadAllLinesAsync(
            System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsSHA256FileName));

        foreach (string line in lines)
        {
            ReadOnlySpan<char> span = line.AsSpan();
            int spaceIdx = span.IndexOf(' ');
            using (Assert.EnterMultipleScope())
            {
                Assert.That(spaceIdx, Is.GreaterThan(0), "each line should be '<hash> <filename>'");
                Assert.That(span[(spaceIdx + 1)..].EndsWith(EraPathUtils.FileExtension.AsSpan()), Is.True);
            }
        }
    }

    [Test]
    public async Task Export_WhenCalled_EraFilesHaveCorrectExtension()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Is.Not.Empty);
        foreach (string eraFile in eraFiles)
        {
            Assert.That(eraFile, Does.EndWith(EraPathUtils.FileExtension));
        }
    }

    [Test]
    public async Task Export_WithToExceedingHeadBlock_ThrowsArgumentException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(5).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 999UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WithFromExceedingTo_ThrowsArgumentException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 20UL, 10UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WhenReceiptsStorageReturnsNull_ThrowsEraException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(10)
            .AddSingleton<IReceiptStorage>(Substitute.For<IReceiptStorage>())
            .Build();

        container.Resolve<IReceiptStorage>()
            .Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>())
            .ReturnsNull();

        IEraExporter sut = container.Resolve<IEraExporter>();
        string tmpDirectory = container.ResolveTempDirPath();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 1UL), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Export_WithToZero_ExportsUpToHead()
    {
        const int chainLength = 32;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, to: 0UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Is.Not.Empty, "exporting to 0 should default to head");
    }

    [Test]
    public void Export_WithDestinationAsExistingFile_ThrowsArgumentException()
    {
        using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(10).Build();

        string tmpFile = container.ResolveTempFilePath();
        System.IO.File.WriteAllText(tmpFile, "existing");

        IEraExporter sut = container.Resolve<IEraExporter>();
        Assert.That(() => sut.Export(tmpFile, 0UL, 0UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WithPostMergeChain_ProducesEpochWithNoTdProofOrAccumulatorEntries()
    {
        const ulong chainLength = 16;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithPostMergeBlockTreeOfLength(chainLength).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        // Export from block 1: genesis is pre-merge in all block trees, so starting at 1 ensures a pure post-merge epoch.
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 1UL, 0UL);

        string[] eraFiles = Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Has.Length.EqualTo(1), "one epoch for blocks 1-15");

        AssertNoPreMergeEntries(eraFiles[0]);
    }

    [Test]
    public async Task Export_WhenPoSSwitcherMarksGenesisPostMerge_ProducesEpochWithNoTdProofOrAccumulatorEntries()
    {
        Block genesis = Build.A.Block.Genesis
            .WithTimestamp(1_742_212_800)
            .WithDifficulty(BlockHeaderBuilder.DefaultDifficulty)
            .TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(genesis);
        blockTree.FindBlock(0UL, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).Returns(genesis);

        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(genesis, true, false).Returns([]);

        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(genesis.Header).Returns(true);

        await using IContainer container = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(blockTree)
            .AddSingleton<IReceiptStorage>(receiptStorage)
            .AddSingleton<IPoSSwitcher>(poSSwitcher)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        string eraFile = Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}").Single();
        AssertNoPreMergeEntries(eraFile);
        poSSwitcher.Received(1).IsPostMerge(genesis.Header);
    }

    [Test]
    public void Export_WhenLastBlockBodyNotAvailable_ThrowsInvalidOperationException()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block head = Build.A.Block.WithNumber(10).TestObject;
        blockTree.Head.Returns(head);
        blockTree.FindBlock(10, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).ReturnsNull();

        using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(1)
            .AddSingleton<IBlockTree>(blockTree)
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 10UL), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Export_WithPartialRange_CreatesOnlyEpochsInRange()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 16UL, 31UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles.Length, Is.EqualTo(1), "only one epoch covers blocks 16-31");
    }

    private static async Task AssertReadsBack(IContainer container, string directory, ulong from, ulong to)
    {
        using IEraStore store = container.Resolve<IEraStoreFactory>().Create(directory, null);
        await AssertReadsBack(container, store, from, to);
    }

    private static async Task AssertReadsBack(IContainer container, IEraStore store, ulong from, ulong to)
    {
        for (ulong number = from; number <= to; number++)
        {
            Block source = container.Resolve<IBlockTree>().FindBlock(number, BlockTreeLookupOptions.None)!;
            (Block? block, TxReceipt[]? receipts) = await store.FindBlockAndReceipts(number);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(block?.Hash, Is.EqualTo(source.Hash), $"block {number}");
                Assert.That(receipts?.Select(static r => r.GasUsedTotal),
                    Is.EqualTo(container.Resolve<IReceiptStorage>().Get(source).Select(static r => r.GasUsedTotal)), $"receipts of block {number}");
            }
        }
    }

    /// <summary>A store over the real directory that lists its files in ordinal order, or in reverse.</summary>
    private static EraStore CreateStore(IContainer container, string directory, bool reversed)
    {
        IFileSystem real = new RealFileSystem();
        IDirectory listing = Substitute.For<IDirectory>();
        listing.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EnumerationOptions>()).Returns(c =>
        {
            IEnumerable<string> files = real.Directory.EnumerateFiles(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<EnumerationOptions>(2)).Order(StringComparer.Ordinal);
            return reversed ? files.Reverse() : files;
        });
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(real.File);
        fileSystem.Directory.Returns(listing);
        return new EraStore(container.Resolve<ISpecProvider>(), container.Resolve<IBlockValidator>(), fileSystem, EraETestModule.TestNetwork, 16, null, directory);
    }

    /// <summary>Copies or moves an era file to the name with its hash in upper case, a separate name only on a case-sensitive file system.</summary>
    private static void RenameToUpperCaseHash(string file, bool keepOriginal)
    {
        string name = Path.GetFileName(file);
        string hash = name.Split('-')[2][..8];
        string variant = Path.Combine(Path.GetDirectoryName(file)!, name.Replace(hash, hash.ToUpperInvariant()));
        Assume.That(variant, Is.Not.EqualTo(file));
        Assume.That(File.Exists(variant), Is.False, "needs a case-sensitive file system");
        if (keepOriginal) File.Copy(file, variant);
        else File.Move(file, variant);
    }

    /// <summary>The real file system, except that File.Move and File.Delete match names ignoring case, as NTFS and APFS do.</summary>
    private static IFileSystem CaseInsensitiveFileSystem()
    {
        IFileSystem real = new RealFileSystem();
        static string? Find(string path) => Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        IFile file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(static c => File.Exists(c.Arg<string>()));
        file.Create(Arg.Any<string>()).Returns(c => real.File.Create(c.Arg<string>()));
        file.ReadAllLines(Arg.Any<string>()).Returns(static c => File.ReadAllLines(c.Arg<string>()));
        file.When(static f => f.Delete(Arg.Any<string>())).Do(static c =>
        {
            if (Find(c.Arg<string>()) is { } existing) File.Delete(existing);
        });
        file.When(static f => f.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(static c =>
        {
            if (Find(c.ArgAt<string>(1)) is { } existing) File.Delete(existing);
            File.Move(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<bool>(2));
        });
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);
        fileSystem.Directory.Returns(real.Directory);
        fileSystem.FileStream.Returns(real.FileStream);
        return fileSystem;
    }

    /// <summary>The real file system, except that File.Move and File.Delete throw for the paths the predicates select.</summary>
    private static IFileSystem CreateFaultyFileSystem(Func<string, bool> failMove, Func<string, bool> failDelete)
    {
        IFileSystem real = new RealFileSystem();
        IFile file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(static c => File.Exists(c.Arg<string>()));
        file.Create(Arg.Any<string>()).Returns(c => real.File.Create(c.Arg<string>()));
        file.ReadAllLines(Arg.Any<string>()).Returns(static c => File.ReadAllLines(c.Arg<string>()));
        file.When(static f => f.Delete(Arg.Any<string>())).Do(c =>
        {
            if (failDelete(c.Arg<string>())) throw new IOException("delete failed");
            File.Delete(c.Arg<string>());
        });
        file.When(static f => f.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(c =>
        {
            if (failMove(c.ArgAt<string>(1))) throw new IOException("move failed");
            File.Move(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<bool>(2));
        });
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);
        fileSystem.Directory.Returns(real.Directory);
        fileSystem.FileStream.Returns(real.FileStream);
        return fileSystem;
    }

    /// <summary>Makes a rewrite of these blocks fail, so an export that succeeds proves the epoch was reused.</summary>
    private static void DropReceipts(IContainer container, ulong from, ulong to)
    {
        for (ulong number = from; number <= to; number++)
            container.Resolve<IReceiptStorage>().RemoveReceipts(container.Resolve<IBlockTree>().FindBlock(number, BlockTreeLookupOptions.None)!);
    }

    private static void AssertNoPreMergeEntries(string eraFile)
    {
        List<ushort> types = [];
        foreach (EraFileFormatComplianceTests.EntryRecord entry in EraFileFormatComplianceTests.ReadAllEntries(eraFile))
        {
            types.Add(entry.Type);
        }

        Assert.That(types, Does.Not.Contain(EntryTypes.Proof), "post-merge epochs have no Proof entries");
        Assert.That(types, Does.Not.Contain(EntryTypes.TotalDifficulty), "post-merge epochs have no TotalDifficulty entries");
        Assert.That(types, Does.Not.Contain(EntryTypes.AccumulatorRoot), "post-merge epochs have no AccumulatorRoot entry");
    }
}
