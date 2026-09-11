// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Merge.Plugin.GC;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class CollectionsPerDecommitTests
{
    [Test]
    public void Negative_sentinel_binds_without_overflow()
    {
        ConfigProvider configProvider = new();
        configProvider.AddSource(new ArgsConfigSource(new Dictionary<string, string>
        {
            { "Merge.CollectionsPerDecommit", "-1" }
        }));

        IMergeConfig mergeConfig = configProvider.GetConfig<IMergeConfig>();

        Assert.That(mergeConfig.CollectionsPerDecommit, Is.EqualTo(-1));
    }

    [Test]
    public void NoGCStrategy_uses_negative_sentinel_to_disable_decommit() =>
        Assert.That(NoGCStrategy.Instance.CollectionsPerDecommit, Is.EqualTo(-1));
}
