// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;

namespace Ethereum.ConsensusSpec.Test;

/// <remarks>Production exposes the combined process_rewards_and_penalties, not individual delta functions.</remarks>
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
    [Test]
    public void Every_fork_and_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsSomeVector(
            FuluDriverSupport.TestedCases<RewardsCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases),
            static testCase => $"{testCase.Fork}/{testCase.Handler}",
            Run);

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

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.HandlerCases(preset, ConsensusSpecArchive.StateTransitionForks, "rewards", "pre.ssz_snappy",
            static (p, fork, handler, path, name) => new RewardsCase(p.ToString(), fork, handler, path, name));
}

public readonly record struct RewardsCase(string Preset, string Fork, string Handler, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
