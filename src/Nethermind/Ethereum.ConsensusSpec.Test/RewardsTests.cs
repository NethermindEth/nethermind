// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>rewards</c> suite (tests/formats/rewards/README.md) for every fork in
/// <see cref="ConsensusSpecArchive.StateTransitionForks"/>. The vectors give the <c>Deltas</c> of each
/// <c>get_flag_index_deltas</c> and of <c>get_inactivity_penalty_deltas</c>; this repo computes them only inside
/// <c>process_rewards_and_penalties</c>, so each vector checks that the balances it leaves equal the pre-state's
/// balances with every delta applied in the spec's order.
/// </summary>
[TestFixture]
public class RewardsTests
{
    /// <summary>The flag deltas in <c>PARTICIPATION_FLAG_WEIGHTS</c> order, then the inactivity penalty deltas, as <c>process_rewards_and_penalties</c> applies them.</summary>
    private static readonly string[] DeltasFiles = ["source_deltas", "target_deltas", "head_deltas", "inactivity_penalty_deltas"];

    private static readonly string[] Handlers = ["basic", "inactivity_scores", "leak", "random"];

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(RewardsCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(RewardsCase testCase) => Execute(testCase);

    // A wrong suite path or an emptied case source enumerates zero vectors; a handler this driver does not enumerate never runs; both stay green.
    [Test]
    public void Every_fork_and_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<RewardsCase> cases = FuluDriverSupport.TestedCases<RewardsCase>(preset, MinimalCases, MainnetCases);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cases.Select(static testCase => testCase.Fork).Distinct(), Is.EquivalentTo(ConsensusSpecArchive.StateTransitionForks));
            foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
            {
                Assert.That(ConsensusSpecArchive.SubDirs(ConsensusSpecArchive.SuitePath(preset, fork, "rewards")).Select(Path.GetFileName),
                    Is.EquivalentTo(Handlers), $"{fork} rewards handlers in the archive");
            }
        }
    }

    // Not-implemented vectors are Inconclusive, so a driver that reports every mainnet vector that way still runs green.
    [Test]
    public void Every_fork_and_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsSomeVector(
            FuluDriverSupport.TestedCases<RewardsCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases),
            static testCase => $"{testCase.Fork}/{testCase.Handler}",
            Run);

    /// <summary><c>process_rewards_and_penalties</c> applying known deltas: the rewards before the penalties of each validator, component by component.</summary>
    [Test]
    public void Expected_balances_apply_each_component_in_order_and_saturate_at_zero()
    {
        (ulong[] Rewards, ulong[] Penalties)[] deltas =
        [
            ([5, 0], [0, 7]),
            ([0, 3], [12, 0]),
        ];

        Assert.That(ExpectedBalances([4, 6], deltas), Is.EqualTo(new ulong[] { 0, 3 }), "4 + 5 - 12 and 6 - 7 both saturate at 0, then the second gains 3");
    }

    private static void Execute(RewardsCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("rewards", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(RewardsCase testCase)
    {
        FuluDriverSupport.RequireMainnetPreset(testCase.Preset);
        switch (FuluDriverSupport.RequireForkDriver(testCase.Fork))
        {
            case ForkDriver<BeaconStateFulu> fulu:
                Run(testCase, fulu, static state => state.Balances!, static state => state.GetCurrentEpoch(), EpochProcessing.ProcessRewardsAndPenalties);
                break;
            case ForkDriver<BeaconStateGloas> gloas:
                Run(testCase, gloas, static state => state.Balances!, static state => state.GetCurrentEpoch(), GloasEpochProcessing.ProcessRewardsAndPenalties);
                break;
            case ForkDriver other:
                throw new NotImplementedInDriverException($"fork '{other.Fork}' has no state type this suite knows.");
        }
    }

    private static void Run<TState>(RewardsCase testCase, ForkDriver<TState> driver, Func<TState, ulong[]> balancesOf, Func<TState, ulong> epochOf, Action<TState, EpochCache> processRewardsAndPenalties)
        where TState : class
    {
        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        // process_rewards_and_penalties returns before applying any delta in the genesis epoch.
        if (epochOf(state) == Presets.GenesisEpoch)
            throw new NotImplementedInDriverException("the pre-state is in the genesis epoch, where process_rewards_and_penalties applies no delta, so the vector's deltas are unobservable here.");

        (ulong[] Rewards, ulong[] Penalties)[] deltas = [.. DeltasFiles.Select(file => DecodeDeltas(Path.Combine(testCase.CasePath, file + ".ssz_snappy")))];
        ulong[] expected = ExpectedBalances([.. balancesOf(state)], deltas);

        processRewardsAndPenalties(state, driver.NewCache());

        Assert.That(balancesOf(state), Is.EqualTo(expected));
    }

    /// <summary>The spec's <c>increase_balance</c> and <c>decrease_balance</c> over every component of <paramref name="deltas"/>, in order.</summary>
    private static ulong[] ExpectedBalances(ulong[] balances, (ulong[] Rewards, ulong[] Penalties)[] deltas)
    {
        foreach ((ulong[] rewards, ulong[] penalties) in deltas)
        {
            if (rewards.Length != balances.Length || penalties.Length != balances.Length)
                throw new InvalidDataException($"deltas cover {rewards.Length}/{penalties.Length} validators, the registry {balances.Length}");

            for (int i = 0; i < balances.Length; i++)
            {
                balances[i] = checked(balances[i] + rewards[i]);
                balances[i] = balances[i] > penalties[i] ? balances[i] - penalties[i] : 0;
            }
        }

        return balances;
    }

    /// <summary>Decodes an SSZ <c>Deltas</c> container: two offsets, then the <c>rewards</c> and <c>penalties</c> lists of uint64.</summary>
    private static (ulong[] Rewards, ulong[] Penalties) DecodeDeltas(string path)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(path);
        int rewardsOffset = BinaryPrimitives.ReadInt32LittleEndian(ssz);
        int penaltiesOffset = BinaryPrimitives.ReadInt32LittleEndian(ssz.AsSpan(4));
        if (rewardsOffset != 8 || penaltiesOffset < rewardsOffset || penaltiesOffset > ssz.Length || (penaltiesOffset - rewardsOffset) % 8 != 0 || (ssz.Length - penaltiesOffset) % 8 != 0)
            throw new InvalidDataException($"malformed Deltas in {path}");

        return (ReadGwei(ssz.AsSpan(rewardsOffset, penaltiesOffset - rewardsOffset)), ReadGwei(ssz.AsSpan(penaltiesOffset)));
    }

    private static ulong[] ReadGwei(ReadOnlySpan<byte> bytes)
    {
        ulong[] values = new ulong[bytes.Length / 8];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(i * 8)..]);
        return values;
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled)
            yield break;
        foreach (TestCaseData data in Cases(ConsensusPreset.Mainnet))
            yield return data;
    }

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
        {
            foreach (string handlerDir in ConsensusSpecArchive.SubDirs(ConsensusSpecArchive.SuitePath(preset, fork, "rewards")))
            {
                string handler = Path.GetFileName(handlerDir);
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(handlerDir, "pre.ssz_snappy"))
                {
                    string vectorName = $"{preset}/{fork}/rewards/{handler}/{Path.GetFileName(caseDir)}";
                    yield return new TestCaseData(new RewardsCase(preset.ToString(), fork, handler, caseDir, vectorName)).SetName(vectorName);
                }
            }
        }
    }
}

public readonly record struct RewardsCase(string Preset, string Fork, string Handler, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
