// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Core.Specs;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Proofs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Proofs;

public class ValidatorTests
{
    [Test]
    public void VerifyAccumulator_WithMatchingTrustedAccumulator_ReturnsTrue()
    {
        ValueHash256 root = new("0xaabbccdd00000000000000000000000000000000000000000000000000000000");
        Validator sut = BuildValidator(root);

        bool result = sut.VerifyAccumulator(0, root);

        Assert.That(result, Is.True);
    }

    [Test]
    public void VerifyAccumulator_WithMismatchedTrustedAccumulator_ReturnsFalse()
    {
        ValueHash256 root = new("0xaabbccdd00000000000000000000000000000000000000000000000000000000");
        Validator sut = BuildValidator(root);

        bool result = sut.VerifyAccumulator(0,
            new ValueHash256("0x1122334400000000000000000000000000000000000000000000000000000000"));

        Assert.That(result, Is.False);
    }

    [Test]
    public void VerifyAccumulator_WhenNoTrustedAccumulatorsProvided_ReturnsTrue()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.BeaconChainGenesisTimestamp.Returns((ulong?)1606824023UL);
        Validator sut = new(specProvider, null, null, null);

        bool result = sut.VerifyAccumulator(0,
            new ValueHash256("0xaabbccdd00000000000000000000000000000000000000000000000000000000"));

        Assert.That(result, Is.True);
    }

    [Test]
    public void VerifyAccumulator_WhenAccumulatorNotFoundForEpoch_ThrowsEraVerificationException()
    {
        ValueHash256 root = new("0xaabbccdd00000000000000000000000000000000000000000000000000000000");
        Validator sut = BuildValidator(root);

        // Epoch 1 requires index 1 in the trusted set, but we only have 1 entry (index 0)
        Assert.That(() => { sut.VerifyAccumulator(8192, root); }, Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task VerifyBlocksRootContext_HistoricalSummaries_LooksUpTheSummaryForTheBlockTimestampInSeconds()
    {
        // The first Shanghai block opens summary 758: (1681338455 - 1606824023) / 12 / 8192.
        using BlocksRootContext context = new(MainnetSpecProvider.ParisBlockNumber + 1, MainnetSpecProvider.ShanghaiBlockTimestamp, MainnetSpecProvider.Instance);
        context.ProcessBlock(Build.A.Block.WithNumber(MainnetSpecProvider.ParisBlockNumber + 1).TestObject,
            new ValueHash256("0xaabbccdd00000000000000000000000000000000000000000000000000000000"),
            new ValueHash256("0x1122334400000000000000000000000000000000000000000000000000000000"));
        context.FinalizeContext();

        IHistoricalSummariesProvider summaries = Substitute.For<IHistoricalSummariesProvider>();
        summaries.GetHistoricalSummary(758, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(context.HistoricalSummary);
        Validator sut = new(MainnetSpecProvider.Instance, null, null, summaries);

        await sut.VerifyBlocksRootContext(context);

        await summaries.Received(1).GetHistoricalSummary(758, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [TestCase(false, 10, TestName = "VerifyBlocksRootContext_HistoricalSummaries_UsesTwelveSecondSlots")]
    [TestCase(true, 11, TestName = "VerifyBlocksRootContext_HistoricalSummaries_UsesEip8198Slots")]
    public async Task VerifyBlocksRootContext_HistoricalSummaries_FindsTheSlotAcrossEip8198(bool eip8198Scheduled, int expectedIndex)
    {
        const ulong genesis = 1_606_824_023;
        const ulong fork = genesis + 10 * 8192 * 12; // slot 81920
        const ulong timestamp = fork + 8192 * 10;    // slot 90112 with 10 s slots, 88746 with 12 s
        (ForkActivation, IReleaseSpec)[] transitions = eip8198Scheduled
            ? [((ForkActivation)0, Shanghai.Instance), (new ForkActivation(0, fork), new OverridableReleaseSpec(Shanghai.Instance) { IsEip8198Enabled = true })]
            : [((ForkActivation)0, Shanghai.Instance)];
        CustomSpecProvider specProvider = new(transitions) { BeaconChainGenesisTimestamp = genesis, Eip8198SlotDurationMs = 10_000 };

        using BlocksRootContext context = new(1, timestamp, specProvider);
        context.ProcessBlock(Build.A.Block.WithNumber(1).TestObject, default, default);
        context.FinalizeContext();

        IHistoricalSummariesProvider summaries = Substitute.For<IHistoricalSummariesProvider>();
        summaries.GetHistoricalSummary(expectedIndex, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(context.HistoricalSummary);

        await new Validator(specProvider, null, null, summaries).VerifyBlocksRootContext(context);

        await summaries.Received(1).GetHistoricalSummary(expectedIndex, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static Validator BuildValidator(ValueHash256 trustedRoot)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.BeaconChainGenesisTimestamp.Returns((ulong?)1606824023UL);
        return new Validator(specProvider, new List<ValueHash256> { trustedRoot }, null, null);
    }
}
