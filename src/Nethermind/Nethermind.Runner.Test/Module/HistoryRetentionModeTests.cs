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

    [Test]
    public void SinceBlockWithoutAStartingBlock_IsRefused() =>
        Assert.That(() => Build(Config(HistoryRetentionMode.SinceBlock, 0)), Throws.TypeOf<InvalidConfigurationException>(),
            "since block 0 is genesis, which is None spelled differently, so it must be refused rather than accepted as a no-op");

    [Test]
    public void AStartingBlockWithoutTheMode_IsRefused() =>
        Assert.That(() => Build(Config(HistoryRetentionMode.None, 0, sinceBlock: 15_000_000)), Throws.TypeOf<InvalidConfigurationException>(),
            "a starting block only means something under SinceBlock; silently ignoring it would keep history the operator asked to drop");

    [Test]
    public void SinceBlockWithAWindowSize_IsRefused() =>
        Assert.That(() => Build(Config(HistoryRetentionMode.SinceBlock, 1024, sinceBlock: 15_000_000)), Throws.TypeOf<InvalidConfigurationException>(),
            "a fixed floor and a rolling window cannot both hold; the block count belongs to Rolling alone");

    [TestCase(HistoryRetentionMode.None, 0UL, 0UL, false, TestName = "Unbounded")]
    [TestCase(HistoryRetentionMode.Rolling, 1024UL, 0UL, true, TestName = "Windowed")]
    [TestCase(HistoryRetentionMode.SinceBlock, 0UL, 15_000_000UL, true, TestName = "SinceBlock")]
    public void TheModeDecidesWhetherHistoryIsWindowed(HistoryRetentionMode mode, ulong blocks, ulong sinceBlock, bool windowed)
    {
        FlatDbConfig config = Config(mode, blocks, sinceBlock);

        Assert.That(config.IsHistoryWindowed(), Is.EqualTo(windowed),
            "the row format and the capture-off refusal both key off this one answer; the deletion tuning and the pruner are the narrower Rolling question");
        Assert.That(() => Build(config).Dispose(), Throws.Nothing,
            "a mode paired with the block count it requires is a complete, accepted configuration");
    }

    private static FlatDbConfig Config(HistoryRetentionMode mode, ulong blocks, ulong sinceBlock = 0) => new()
    {
        Enabled = true,
        HistoryEnabled = true,
        HistoryRetention = mode,
        HistoryRetentionBlocks = blocks,
        HistoryRetentionSinceBlock = sinceBlock
    };

    private static IContainer Build(FlatDbConfig config) =>
        new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();
}
