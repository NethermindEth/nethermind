// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class EpochProcessingTests
{
    private static readonly Dictionary<string, Action<BeaconStateFulu, EpochCache>> Handlers = new(StringComparer.Ordinal)
    {
        ["effective_balance_updates"] = (state, cache) => EpochProcessing.ProcessEffectiveBalanceUpdates(state, cache),
        ["eth1_data_reset"] = (state, _) => EpochProcessing.ProcessEth1DataReset(state),
        ["historical_summaries_update"] = (state, _) => EpochProcessing.ProcessHistoricalSummariesUpdate(state),
        ["inactivity_updates"] = (state, _) => EpochProcessing.ProcessInactivityUpdates(state),
        ["justification_and_finalization"] = (state, cache) => EpochProcessing.ProcessJustificationAndFinalization(state, cache),
        ["participation_flag_updates"] = (state, _) => EpochProcessing.ProcessParticipationFlagUpdates(state),
        ["pending_consolidations"] = (state, _) => EpochProcessing.ProcessPendingConsolidations(state),
        ["pending_deposits"] = (state, cache) => EpochProcessing.ProcessPendingDeposits(state, cache),
        ["proposer_lookahead"] = (state, _) => EpochProcessing.ProcessProposerLookahead(state),
        ["randao_mixes_reset"] = (state, _) => EpochProcessing.ProcessRandaoMixesReset(state),
        ["registry_updates"] = (state, cache) => EpochProcessing.ProcessRegistryUpdates(state, cache),
        ["rewards_and_penalties"] = (state, cache) => EpochProcessing.ProcessRewardsAndPenalties(state, cache),
        ["slashings"] = (state, cache) => EpochProcessing.ProcessSlashings(state, cache),
        ["slashings_reset"] = (state, _) => EpochProcessing.ProcessSlashingsReset(state),
        ["sync_committee_updates"] = (state, _) => EpochProcessing.ProcessSyncCommitteeUpdates(state),
    };

    private static readonly Dictionary<string, Action<BeaconStateGloas, EpochCache>> GloasHandlers = new(StringComparer.Ordinal)
    {
        ["builder_pending_payments"] = GloasEpochProcessing.ProcessBuilderPendingPayments,
        ["effective_balance_updates"] = GloasEpochProcessing.ProcessEffectiveBalanceUpdates,
        ["eth1_data_reset"] = (state, _) => GloasEpochProcessing.ProcessEth1DataReset(state),
        ["historical_summaries_update"] = (state, _) => GloasEpochProcessing.ProcessHistoricalSummariesUpdate(state),
        ["inactivity_updates"] = (state, _) => GloasEpochProcessing.ProcessInactivityUpdates(state),
        ["justification_and_finalization"] = GloasEpochProcessing.ProcessJustificationAndFinalization,
        ["participation_flag_updates"] = (state, _) => GloasEpochProcessing.ProcessParticipationFlagUpdates(state),
        ["pending_consolidations"] = (state, _) => GloasEpochProcessing.ProcessPendingConsolidations(state),
        ["pending_deposits"] = GloasEpochProcessing.ProcessPendingDeposits,
        // The churn-boundary cases of process_pending_deposits, generated under their own handler name.
        ["pending_deposits_churn"] = GloasEpochProcessing.ProcessPendingDeposits,
        ["proposer_lookahead"] = (state, _) => GloasEpochProcessing.ProcessProposerLookahead(state),
        ["ptc_window"] = (state, _) => GloasEpochProcessing.ProcessPtcWindow(state),
        ["randao_mixes_reset"] = (state, _) => GloasEpochProcessing.ProcessRandaoMixesReset(state),
        ["registry_updates"] = GloasEpochProcessing.ProcessRegistryUpdates,
        ["rewards_and_penalties"] = GloasEpochProcessing.ProcessRewardsAndPenalties,
        ["slashings"] = GloasEpochProcessing.ProcessSlashings,
        ["slashings_reset"] = (state, _) => GloasEpochProcessing.ProcessSlashingsReset(state),
        ["sync_committee_updates"] = (state, _) => GloasEpochProcessing.ProcessSyncCommitteeUpdates(state),
    };

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(EpochProcessingCase testCase) => Execute(testCase);
    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(EpochProcessingCase testCase) => Execute(testCase);

    /// <summary>Handlers with no mainnet vectors at <see cref="ConsensusSpecArchive.Version"/>; only the minimal preset generates them.</summary>
    private static readonly string[] MinimalOnlySubTransitions = ["sync_committee_updates"];

    private static readonly Dictionary<string, string[]> SubTransitionsAbsentByFork = new(StringComparer.Ordinal)
    {
        ["electra"] = ["proposer_lookahead"],
    };
    [Test]
    public void Every_fork_and_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<EpochProcessingCase> cases = FuluDriverSupport.TestedCases<EpochProcessingCase>(preset, MinimalCases, MainnetCases);
        string[] absentForPreset = preset == ConsensusPreset.Mainnet ? MinimalOnlySubTransitions : [];
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(cases.Select(static testCase => testCase.Fork).Distinct(), Is.EquivalentTo(ConsensusSpecArchive.StateTransitionForks));
        foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
        {
            IEnumerable<string> table = FuluDriverSupport.RequireForkDriver(fork) is ForkDriver<BeaconStateGloas> ? GloasHandlers.Keys : Handlers.Keys;
            Assert.That(
                cases.Where(testCase => testCase.Fork == fork).Select(static testCase => testCase.SubTransitionName).Distinct(),
                Is.EquivalentTo(table.Except(absentForPreset).Except(SubTransitionsAbsentByFork.GetValueOrDefault(fork, []))),
                $"{fork} sub-transitions");
        }
    }
    [Test]
    public void Every_fork_and_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<EpochProcessingCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases),
            static testCase => $"{testCase.Fork}/{testCase.SubTransitionName}",
            Run);

    private static void Execute(EpochProcessingCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("epoch_processing", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(EpochProcessingCase testCase)
    {
        if (testCase.Preset == nameof(ConsensusPreset.Minimal))
        {
            throw new NotImplementedInDriverException(
                "This repo's BeaconState containers hard-code mainnet-preset-scaled vector bounds, so they cannot decode a " +
                "minimal-preset pre.ssz_snappy at all; this suite only runs for real against the mainnet preset " +
                "(opt in with NETHERMIND_CONSENSUS_SPEC_MAINNET=1).");
        }

        FuluDriverSupport.Dispatch(testCase.Fork, testCase,
            static (testCase, driver) => Run(testCase, driver, Handlers),
            static (testCase, driver) => Run(testCase, driver, GloasHandlers), "epoch_processing handler table");
    }

    private static void Run<TState>(EpochProcessingCase testCase, ForkDriver<TState> driver, Dictionary<string, Action<TState, EpochCache>> handlers)
        where TState : class
    {
        if (!handlers.TryGetValue(testCase.SubTransitionName, out Action<TState, EpochCache>? apply))
            throw new NotImplementedInDriverException($"epoch sub-transition '{testCase.SubTransitionName}' has no handler in this driver.");

        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        EpochCache cache = driver.NewCache();
        // Primes the incremental hasher so the post-state root is taken as a diff against the pre-state, as in production.
        driver.CachedRoot(state, cache);

        // Missing post-state means rejection, including registry_updates uint64-overflow vectors.
        string postPath = Path.Combine(testCase.CasePath, "post.ssz_snappy");
        FuluDriverSupport.AssertTransition(driver, postPath, state, cache,
            () => apply(state, cache), "the sub-transition",
            "expected the sub-transition to succeed, but it threw");
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.HandlerCases(preset, ConsensusSpecArchive.StateTransitionForks, "epoch_processing", "pre.ssz_snappy",
            static (p, fork, handler, path, name) => new EpochProcessingCase(p.ToString(), fork, handler, path, name), strictHandlerDirectory: true);
}

public readonly record struct EpochProcessingCase(string Preset, string Fork, string SubTransitionName, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
