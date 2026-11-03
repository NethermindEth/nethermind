// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;

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
}
