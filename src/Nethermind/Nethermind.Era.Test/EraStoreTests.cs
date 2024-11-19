// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Core.Test.IO;

namespace Nethermind.Era1.Test;

public class EraStoreTests
{
    [TestCase(1000, 16, 32)]
    [TestCase(1000, 16, 64)]
    [TestCase(1000, 50, 100)]
    public async Task SmallestAndLowestBlock_ShouldBeCorrect(int chainLength, int start, int end)
    {
        await using IContainer ctx = await EraTestModule.CreateExportedEraEnv(chainLength, start, end);
        string tmpDirectory = ctx.ResolveTempDirPath();

        IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);

        eraStore.FirstBlock.Should().Be(start);
        eraStore.LastBlock.Should().Be(end);
    }
}
