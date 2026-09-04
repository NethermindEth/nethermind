// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class HistoryRetentionModeTests
{
    [Test]
    public void RollingWithoutAWindowSize_IsRefused() =>
        Assert.That(() => Build(Config(HistoryRetentionMode.Rolling, 0)), Throws.TypeOf<InvalidConfigurationException>(),
            "Rolling with no window size has no floor to prune to, so it must be refused rather than resolved to some default");

    [Test]
    public void AWindowSizeWithoutTheMode_IsRefused() =>
        Assert.That(() => Build(Config(HistoryRetentionMode.None, 1024)), Throws.TypeOf<InvalidConfigurationException>(),
            "a configuration written when the block count alone selected the window must not silently become an unbounded archive");

    [TestCase(HistoryRetentionMode.None, 0UL, false, TestName = "Unbounded")]
    [TestCase(HistoryRetentionMode.Rolling, 1024UL, true, TestName = "Windowed")]
    public void TheModeDecidesWhetherHistoryIsWindowed(HistoryRetentionMode mode, ulong blocks, bool windowed)
    {
        FlatDbConfig config = Config(mode, blocks);

        Assert.That(config.IsHistoryWindowed(), Is.EqualTo(windowed),
            "the row format, the rocksdb deletion tuning and the capture-off refusal all key off this one answer");
        Assert.That(() => Build(config).Dispose(), Throws.Nothing,
            "a mode paired with the block count it requires is a complete, accepted configuration");
    }

    private static FlatDbConfig Config(HistoryRetentionMode mode, ulong blocks) => new()
    {
        Enabled = true,
        HistoryEnabled = true,
        HistoryRetention = mode,
        HistoryRetentionBlocks = blocks
    };

    private static IContainer Build(FlatDbConfig config) =>
        new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();
}
