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

        Assert.That(eraStore.FirstBlock, Is.EqualTo(start));
        Assert.That(eraStore.LastBlock, Is.EqualTo(end));
    }
}
