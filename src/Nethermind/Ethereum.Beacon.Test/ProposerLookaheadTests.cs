// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using NUnit.Framework;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>epoch_processing/proposer_lookahead</c> tests, exercising the EIP-7917
/// <c>process_proposer_lookahead</c> sub-transition and through it
/// <c>compute_proposer_indices</c>/<c>compute_proposer_index</c>/<c>get_seed</c>.
/// </summary>
[TestFixture]
public class ProposerLookaheadTests
{
    [TestCaseSource(nameof(ProposerLookaheadCases))]
    public void Process_proposer_lookahead(string casePath) =>
        BeaconStateTestRunner.RunStateTest(casePath, static state =>
        {
            // Spec process_proposer_lookahead: shift out the first epoch and fill in the last.
            ulong[] lookahead = state.ProposerLookahead!;
            int slotsPerEpoch = (int)Presets.SlotsPerEpoch;
            Array.Copy(lookahead, slotsPerEpoch, lookahead, 0, lookahead.Length - slotsPerEpoch);
            ulong[] lastEpochProposers = state.ComputeProposerIndices(state.GetCurrentEpoch() + Presets.MinSeedLookahead + 1);
            lastEpochProposers.CopyTo(lookahead, lookahead.Length - slotsPerEpoch);
        });

    private static IEnumerable<TestCaseData> ProposerLookaheadCases() =>
        BeaconStateTestRunner.EnumerateCases("fulu", "epoch_processing", "proposer_lookahead");
}
