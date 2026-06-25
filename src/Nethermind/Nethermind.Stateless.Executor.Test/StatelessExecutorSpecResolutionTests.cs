// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stateless.Execution.IO;
using NUnit.Framework;

namespace Nethermind.Stateless.Execution.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class StatelessExecutorSpecResolutionTests
{
    // Real mainnet blocks live far above this height; EEST synthetic fixtures (chain id 1) top out at
    // tiny block numbers. This is the gate StatelessExecutor uses to tell them apart.
    private const ulong MainnetRealBlockThreshold = 1_000_000;

    // The original gate keyed on the block timestamp, which misrouted synthetic chain-id-1 fixtures with
    // realistic timestamps to the real MainnetSpecProvider (resolving the wrong fork). These cases pin the
    // ts at the actual mcopy fixture value, the beacon-root ulong.MaxValue value, and a pre-genesis value.
    [TestCase(0x64903c57UL, TestName = "FixtureTimestamp_June2023")]
    [TestCase(ulong.MaxValue, TestName = "BeaconRootTimestamp_UlongMax")]
    [TestCase(12UL, TestName = "TinyTimestamp")]
    public void Synthetic_mainnet_block_below_threshold_keeps_pinned_active_fork(ulong timestamp)
    {
        const ulong blockNumber = 1;
        BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber).WithTimestamp(timestamp).TestObject;
        ChainConfig config = new() { ChainId = BlockchainIds.Mainnet, ActiveFork = CancunForkPinnedAt(blockNumber, timestamp) };

        ISpecProvider specProvider = StatelessExecutor.GetSpecProvider(config, header);

        // Must NOT fall through to the real mainnet schedule (which at these timestamps resolves
        // Shanghai / Amsterdam, not the pinned Cancun) — that was the regression.
        Assert.That(specProvider, Is.Not.SameAs(MainnetSpecProvider.Instance));
        Assert.That(specProvider.GetSpec(header).Name, Is.EqualTo(Cancun.Instance.Name));
    }

    [Test]
    public void Real_mainnet_block_above_threshold_uses_mainnet_spec_provider()
    {
        const ulong blockNumber = 21_000_000;
        BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber).WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp).TestObject;
        ChainConfig config = new() { ChainId = BlockchainIds.Mainnet, ActiveFork = CancunForkPinnedAt(blockNumber, MainnetSpecProvider.CancunBlockTimestamp) };

        ISpecProvider specProvider = StatelessExecutor.GetSpecProvider(config, header);

        Assert.That(specProvider, Is.SameAs(MainnetSpecProvider.Instance));
    }

    [TestCase(MainnetRealBlockThreshold, true, TestName = "AtThreshold_UsesMainnet")]
    [TestCase(MainnetRealBlockThreshold - 1, false, TestName = "BelowThreshold_UsesActiveFork")]
    public void Mainnet_threshold_is_inclusive_on_block_number(ulong blockNumber, bool expectsMainnetSpecProvider)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber).WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp).TestObject;
        ChainConfig config = new() { ChainId = BlockchainIds.Mainnet, ActiveFork = CancunForkPinnedAt(blockNumber, MainnetSpecProvider.CancunBlockTimestamp) };

        ISpecProvider specProvider = StatelessExecutor.GetSpecProvider(config, header);

        Assert.That(ReferenceEquals(specProvider, MainnetSpecProvider.Instance), Is.EqualTo(expectsMainnetSpecProvider));
    }

    [Test]
    public void Non_mainnet_chain_ignores_block_number_threshold()
    {
        const ulong blockNumber = 21_000_000;
        BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber).WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp).TestObject;
        ChainConfig config = new() { ChainId = BlockchainIds.Sepolia, ActiveFork = CancunForkPinnedAt(blockNumber, MainnetSpecProvider.CancunBlockTimestamp) };

        ISpecProvider specProvider = StatelessExecutor.GetSpecProvider(config, header);

        // The mainnet override is gated on chain id 1 only; other chains stay on the ActiveFork path.
        Assert.That(specProvider, Is.Not.SameAs(MainnetSpecProvider.Instance));
        Assert.That(specProvider.ChainId, Is.EqualTo(BlockchainIds.Sepolia));
    }

    // Builds an ActiveFork pinned to Cancun (a fork present on every mainnet-compatible schedule), then
    // re-stamps its activation onto the block's own point — mirroring how StatelessInputGen bakes the fork.
    private static ForkConfig CancunForkPinnedAt(ulong blockNumber, ulong timestamp)
    {
        BlockHeader cancunHeader = Build.A.BlockHeader
            .WithNumber((ulong)MainnetSpecProvider.ParisBlockNumber + 2)
            .WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp)
            .TestObject;

        ForkConfig fork = ForkConfig.From(cancunHeader, MainnetSpecProvider.Instance);
        fork.Activation = SszForkActivation.From(new ForkActivation(blockNumber, timestamp));
        return fork;
    }
}
