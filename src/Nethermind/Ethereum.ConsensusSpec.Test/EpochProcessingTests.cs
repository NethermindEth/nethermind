// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>epoch_processing</c> suite: each sub-transition folder maps directly to
/// one <c>EpochProcessing.Process*</c> method, applied to the fixture's pre-state and compared by root
/// against its post-state. Driven for the same forks as <see cref="OperationsTests"/>, through the same
/// <see cref="ForkDriver"/>; no sub-transition reads the proposer index, so the Electra carry-through
/// needs nothing beyond the fork's state shape.
/// </summary>
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

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(EpochProcessingCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(EpochProcessingCase testCase) => Execute(testCase);

    private static void Execute(EpochProcessingCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("epoch_processing", testCase.Fork, testCase.Preset, testCase.VectorName, () =>
        {
            if (testCase.Preset == nameof(ConsensusPreset.Minimal))
            {
                throw new NotImplementedInDriverException(
                    "This repo's BeaconState containers hard-code mainnet-preset-scaled vector bounds, so they cannot decode a " +
                    "minimal-preset pre.ssz_snappy at all; this suite only runs for real against the mainnet preset " +
                    "(opt in with NETHERMIND_CONSENSUS_SPEC_MAINNET=1).");
            }

            ForkDriver driver = FuluDriverSupport.RequireForkDriver(testCase.Fork);

            if (!Handlers.TryGetValue(testCase.SubTransitionName, out Action<BeaconStateFulu, EpochCache>? apply))
                throw new NotImplementedInDriverException($"epoch sub-transition '{testCase.SubTransitionName}' has no handler in this driver.");

            BeaconStateFulu state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
            EpochCache cache = driver.NewCache();

            // Most epoch_processing vectors are unconditional (no invalid-input case: a sub-transition
            // is a mechanical fold over existing state, not a signature or bounds check on attacker
            // input) - but some ARE, e.g. registry_updates/invalid_large_withdrawable_epoch supplies a
            // validator whose churn-adjusted exit epoch overflows uint64 and carries no post.ssz_snappy,
            // meaning the reference implementation also cannot produce a valid post-state for it. Absent
            // post.ssz_snappy is therefore "expect this sub-transition to fail", exactly like operations
            // and sanity/blocks - discovered by this suite's own mutation-testing pass, not assumed.
            string postPath = Path.Combine(testCase.CasePath, "post.ssz_snappy");
            bool expectSuccess = File.Exists(postPath);

            Exception? thrown = null;
            try { apply(state, cache); }
            catch (Exception ex) { thrown = ex; }

            if (expectSuccess)
            {
                if (thrown is not null)
                    Assert.Fail($"expected the sub-transition to succeed, but it threw: {thrown}");

                FuluDriverSupport.AssertPostStateRoot(driver, postPath, state);
            }
            else if (thrown is null)
            {
                Assert.Fail("expected the sub-transition to be rejected as invalid, but it completed without error");
            }
        });

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
            string? epochProcessingRoot = ConsensusSpecArchive.SuitePath(preset, fork, "epoch_processing");
            if (epochProcessingRoot is null)
                continue;

            foreach (string subDir in Directory.GetDirectories(epochProcessingRoot))
            {
                string subName = Path.GetFileName(subDir);
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(subDir, "pre.ssz_snappy"))
                {
                    string vectorName = $"{preset}/{fork}/epoch_processing/{subName}/{Path.GetFileName(caseDir)}";
                    EpochProcessingCase testCase = new(preset.ToString(), fork, subName, caseDir, vectorName);
                    yield return new TestCaseData(testCase).SetName(vectorName);
                }
            }
        }
    }
}

public readonly record struct EpochProcessingCase(string Preset, string Fork, string SubTransitionName, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
