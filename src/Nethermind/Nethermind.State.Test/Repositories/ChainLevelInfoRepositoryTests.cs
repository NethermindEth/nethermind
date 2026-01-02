// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Repositories;
using NUnit.Framework;

namespace Nethermind.Store.Test.Repositories;

public class ChainLevelInfoRepositoryTests
{
    [Test]
    public void TestMultiGet()
    {
        ChainLevelInfoRepository repository = new ChainLevelInfoRepository(new MemDb());

        ChainLevelInfo level1 = new ChainLevelInfo(false, new BlockInfo(TestItem.KeccakA, 0));
        ChainLevelInfo level10 = new ChainLevelInfo(false, new BlockInfo(TestItem.KeccakB, 0));

        {
            using var _ = repository.StartBatch();
            repository.PersistLevel(1, level1);
            repository.PersistLevel(10, level10);
        }

        using IOwnedReadOnlyList<ChainLevelInfo> levels = repository.MultiLoadLevel(new ArrayPoolListRef<long>(2, 1, 10));
        levels[0].Should().BeEquivalentTo(level1);
        levels[1].Should().BeEquivalentTo(level10);
    }
}
