// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>epoch_processing</c> tests: each fixture exercises a single
/// <c>process_epoch</c> sub-transition in isolation. The <c>proposer_lookahead</c> handler is
/// covered separately by <see cref="ProposerLookaheadTests"/>.
/// </summary>
[TestFixture]
public class EpochProcessingTests
{
    private static readonly Dictionary<string, Action<BeaconStateFulu, EpochCache>> Handlers = new()
    {
        ["justification_and_finalization"] = EpochProcessing.ProcessJustificationAndFinalization,
        ["inactivity_updates"] = static (state, _) => EpochProcessing.ProcessInactivityUpdates(state),
        ["rewards_and_penalties"] = EpochProcessing.ProcessRewardsAndPenalties,
        ["registry_updates"] = EpochProcessing.ProcessRegistryUpdates,
        ["slashings"] = EpochProcessing.ProcessSlashings,
        ["eth1_data_reset"] = static (state, _) => EpochProcessing.ProcessEth1DataReset(state),
        ["pending_deposits"] = EpochProcessing.ProcessPendingDeposits,
        ["pending_consolidations"] = static (state, _) => EpochProcessing.ProcessPendingConsolidations(state),
        ["effective_balance_updates"] = EpochProcessing.ProcessEffectiveBalanceUpdates,
        ["slashings_reset"] = static (state, _) => EpochProcessing.ProcessSlashingsReset(state),
        ["randao_mixes_reset"] = static (state, _) => EpochProcessing.ProcessRandaoMixesReset(state),
        ["historical_summaries_update"] = static (state, _) => EpochProcessing.ProcessHistoricalSummariesUpdate(state),
        ["participation_flag_updates"] = static (state, _) => EpochProcessing.ProcessParticipationFlagUpdates(state),
        ["sync_committee_updates"] = static (state, _) => EpochProcessing.ProcessSyncCommitteeUpdates(state),
    };

    [TestCaseSource(nameof(EpochProcessingCases))]
    public void Epoch_processing(string handler, string casePath) =>
        BeaconStateTestRunner.RunStateTest(casePath, state => Handlers[handler](state, new EpochCache()));

    private static IEnumerable<TestCaseData> EpochProcessingCases()
    {
        foreach (string handler in Handlers.Keys)
        {
            foreach (TestCaseData testCase in BeaconStateTestRunner.EnumerateCases("fulu", "epoch_processing", handler))
            {
                yield return new TestCaseData(handler, testCase.Arguments[0]).SetName(testCase.TestName);
            }
        }
    }
}
