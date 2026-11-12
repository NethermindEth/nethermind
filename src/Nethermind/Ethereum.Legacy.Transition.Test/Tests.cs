// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;
using NUnit.Framework;

namespace Ethereum.Legacy.Transition.Test;

public class MetaTests : DirectoryMetaTests<BcPrefix>
{
    protected override string GetTestsDirectory() => Path.Combine(base.GetTestsDirectory(), "Tests");
}

public class ArrowGlacierToParis : LegacyBlockchainTestFixture<ArrowGlacierToParis>;

[NonParallelizable]
public class BerlinToLondon : LegacyBlockchainTestFixture<BerlinToLondon>;

public class ByzantiumToConstantinopleFix : LegacyBlockchainTestFixture<ByzantiumToConstantinopleFix>;

public class EIP158ToByzantium : LegacyBlockchainTestFixture<EIP158ToByzantium>;

public class FrontierToHomestead : LegacyBlockchainTestFixture<FrontierToHomestead>;

public class HomesteadToDao : LegacyBlockchainTestFixture<HomesteadToDao>;

public class HomesteadToEIP150 : LegacyBlockchainTestFixture<HomesteadToEIP150>;

public class MergeToShanghai : LegacyBlockchainTestFixture<MergeToShanghai>;

[TestFixture]
public class ForkClassificationTests : BlockchainTestBase
{
    private static IEnumerable<TestCaseData> PostMergeClassificationCases()
    {
        yield return new TestCaseData(London.Instance, false).SetName("London is pre-merge");
        yield return new TestCaseData(ArrowGlacier.Instance, false).SetName("ArrowGlacier is pre-merge");
        yield return new TestCaseData(GrayGlacier.Instance, false).SetName("GrayGlacier is pre-merge");
        yield return new TestCaseData(Paris.Instance, true).SetName("Paris is post-merge");
        yield return new TestCaseData(LondonGnosis.Instance, false).SetName("LondonGnosis is pre-merge");
        yield return new TestCaseData(ShanghaiGnosis.Instance, true).SetName("ShanghaiGnosis is post-merge");
    }

    [TestCaseSource(nameof(PostMergeClassificationCases))]
    public void IsPostMergeSpec_returns_expected_value(IReleaseSpec spec, bool expectedIsPostMerge) =>
        Assert.That(IsPostMergeSpec(spec), Is.EqualTo(expectedIsPostMerge));
}

/// <summary>
/// A retesteth <c>{from}To{to}AtDiff{hex}</c> network moves to proof of stake at a terminal total difficulty rather
/// than at a block number or timestamp.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TotalDifficultyTransitionTests : BlockchainTestBase
{
    private const string ZeroHash = "0x0000000000000000000000000000000000000000000000000000000000000000";
    private const string WrongPosException = "BlockException.IMPORT_IMPOSSIBLE_PARIS_WRONG_POS";
    private const string WrongPosBlockHash = "0x4c655cdc61ae3d4619e2e17fde7ba217ee475b8d27657b1ff3ded66630d63283";
    private const string PosOnNonTerminalParentError = "A proof-of-stake block cannot follow a non-terminal proof-of-work parent.";
    private const string WrongPowException = "BlockException.IMPORT_IMPOSSIBLE_PARIS_WRONG_POW";
    private const string WrongPowBlockHash = "0x7066598dab52684e5afa7bdffa60775287ee1b32a75fa97af9970a54c519b60e";
    private const string PowOnTerminalParentError = "A proof-of-work block cannot follow a terminal or proof-of-stake parent.";

    private static string BlockRejectionFixture =>
        Path.Combine(AppContext.BaseDirectory, "Tests", "bcArrowGlacierToParis", "powToPosBlockRejection.json");

    [TestCase("ArrowGlacierToParisAtDiffC0000", 0xC0000UL)]
    [TestCase("ArrowGlacierToParisAtDiff1", 1UL)]
    [TestCase("ArrowGlacierToParisAtDiffabc", 0xABCUL)]
    public void Network_at_a_total_difficulty_reads_it_as_the_terminal_total_difficulty(string network, ulong expected)
    {
        BlockchainTest test = Load(network);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.TransitionTerminalTotalDifficulty, Is.EqualTo((UInt256)expected));
            Assert.That(test.TransitionForkActivation, Is.Null, "a total difficulty is not a block number");
            Assert.That(test.Network, Is.SameAs(ArrowGlacier.Instance));
            Assert.That(test.NetworkAfterTransition, Is.SameAs(Paris.Instance));
        }
    }

    [TestCase("ArrowGlacierToParisAtDiff")]
    [TestCase("ArrowGlacierToParisAtDiffC000G")]
    [TestCase("ArrowGlacierToParisAtDiff0xC00000")]
    public void Network_at_a_total_difficulty_without_hex_digits_does_not_load(string network) =>
        Assert.Throws<FormatException>(() => Load(network));

    [Test]
    public void Network_at_a_block_number_keeps_its_block_activation()
    {
        BlockchainTest test = Load("BerlinToLondonAt5");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.TransitionForkActivation, Is.EqualTo(new ForkActivation(5)));
            Assert.That(test.TransitionTerminalTotalDifficulty, Is.Null);
        }
    }

    [Test]
    public void Spec_provider_carries_the_terminal_total_difficulty_and_keeps_the_pre_merge_fork()
    {
        ISpecProvider specProvider = CreateSpecProvider(Load("ArrowGlacierToParisAtDiffC0000"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(specProvider.TerminalTotalDifficulty, Is.EqualTo((UInt256)0xC0000));
            Assert.That(specProvider.MergeBlockNumber, Is.Null, "the merge block is found while the chain runs");
            Assert.That(specProvider.GetSpec(new ForkActivation(1)), Is.SameAs(ArrowGlacier.Instance));
            Assert.That(specProvider.GetSpec(new ForkActivation(1_000_000)), Is.SameAs(ArrowGlacier.Instance));
        }
    }

    [TestCase("LondonToArrowGlacierAtDiffC0000", Description = "ends before the merge")]
    [TestCase("ShanghaiToParisAtDiffC0000", Description = "starts after the merge")]
    [TestCase("BerlinToParisAtDiffC0000", Description = "starts without EIP-1559")]
    public void Spec_provider_refuses_a_total_difficulty_transition_other_than_from_a_pre_merge_fork_with_EIP_1559_to_Paris(string network)
    {
        BlockchainTest test = Load(network);

        Assert.Throws<NotSupportedException>(() => CreateSpecProvider(test));
    }

    /// <remarks>
    /// Neither of these fixture blocks reaches the main chain either way. With its expected exception removed, the run
    /// fails only if pre-validation rejects it: if it passed, the block tree would leave it unprocessed and the run
    /// would still end at the expected last block. The header is validated as decoded, without a total difficulty, so
    /// the rejection comes from the merge header rules on its parent: a PoS block needs a terminal or PoS parent, and a
    /// PoW block cannot follow one.
    /// </remarks>
    [TestCase(WrongPosException, WrongPosBlockHash, PosOnNonTerminalParentError, TestName = "{m}(PoS block before the terminal total difficulty)")]
    [TestCase(WrongPowException, WrongPowBlockHash, PowOnTerminalParentError, TestName = "{m}(PoW block after the terminal block)")]
    public async Task Block_on_the_wrong_side_of_the_transition_is_rejected_by_pre_validation(
        string expectedException, string blockHash, string error)
    {
        JsonNode fixture = JsonNode.Parse(await File.ReadAllTextAsync(BlockRejectionFixture))!;
        JsonObject testJson = fixture.AsObject().Single().Value!.AsObject();
        JsonObject block = testJson["blocks"]!.AsArray()
            .Single(b => b!["expectException"]?.GetValue<string>() == expectedException)!.AsObject();
        block.Remove("expectException");

        BlockchainTest test = JsonToEthereumTest.ConvertToBlockchainTests(fixture.ToJsonString()).Single();
        test.Name = BlockRejectionFixture;

        AssertionException exception = await Assert.CatchAsync<AssertionException>(() => RunTest(test));

        Assert.That(exception.Message, Does.Contain($"Unexpected invalid block {blockHash}: {error}"));
    }

    private static BlockchainTest Load(string network) =>
        JsonToEthereumTest.ConvertToBlockchainTests(
            $$"""{ "atDiff": { "network": "{{network}}", "lastblockhash": "{{ZeroHash}}", "pre": {} } }""").Single();
}
