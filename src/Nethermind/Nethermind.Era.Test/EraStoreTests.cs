// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using FluentAssertions;
using Nethermind.Core.Specs;

namespace Nethermind.Era1.Test;

public class EraStoreTests
{
    [TestCase(1000, 16, 32)]
    [TestCase(1000, 16, 64)]
    [TestCase(1000, 50, 100)]
    public async Task SmallestAndLowestBlock_ShouldBeCorrect(int chainLength, int start, int end)
    {
        await using IContainer ctx = await EraTestModule.CreateExportedEraEnv(chainLength, start, end);
        TmpDirectory tmpDirectory = ctx.Resolve<TmpDirectory>();

        EraStore eraStore = new EraStore(
               tmpDirectory.DirectoryPath,
               null,
               ctx.Resolve<ISpecProvider>(),
               ctx.ResolveKeyed<string>(EraComponentKeys.NetworkName),
               ctx.Resolve<IFileSystem>(),
               ctx.Resolve<IEraConfig>().MaxEra1Size
            );

        eraStore.FirstBlock.Should().Be(start);
        eraStore.LastBlock.Should().Be(end);
    }
}
