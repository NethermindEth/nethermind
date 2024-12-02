// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.Merge.Plugin.Test;

public static class MergeSyncTestContainerBuilderExtensions
{
    public static ContainerBuilder AddBlockTreeScenario(this ContainerBuilder builder, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder scenarioBuilder)
    {
        return builder
            .AddSingleton<IBlockTree>(scenarioBuilder.NotSyncedTree)
            .AddKeyedSingleton(DbNames.Metadata, scenarioBuilder.NotSyncedTreeBuilder.MetadataDb);
    }
}
