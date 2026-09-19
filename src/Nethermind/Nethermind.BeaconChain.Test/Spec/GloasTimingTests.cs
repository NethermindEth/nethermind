// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Spec;

public class GloasTimingTests
{
    // Values verified against configs/mainnet.yaml on ethereum/consensus-specs `master`
    // (fetched 2026-09-19); each is independently re-derivable as SLOT_DURATION_MS * bps / 10_000.
    [TestCase(GloasTiming.ProposerReorgCutoffBps, 2_000ul)]
    [TestCase(GloasTiming.AttestationDueBps, 3_999ul)] // 12000 * 3333 / 10000 truncates to 3999
    [TestCase(GloasTiming.AggregateDueBps, 8_000ul)]
    [TestCase(GloasTiming.AttestationDueBpsGloas, 3_000ul)]
    [TestCase(GloasTiming.AggregateDueBpsGloas, 6_000ul)]
    [TestCase(GloasTiming.SyncMessageDueBpsGloas, 3_000ul)]
    [TestCase(GloasTiming.ContributionDueBpsGloas, 6_000ul)]
    [TestCase(GloasTiming.PayloadDueBps, 6_000ul)]
    [TestCase(GloasTiming.PayloadAttestationDueBps, 9_000ul)]
    public void DeadlineMs_matches_the_expected_millisecond_offset(ulong basisPoints, ulong expectedMs) =>
        Assert.That(GloasTiming.DeadlineMs(basisPoints), Is.EqualTo(expectedMs));

    [Test]
    public void Gloas_attestation_deadlines_move_earlier_than_their_pre_gloas_counterparts_to_make_room_for_the_payload_phase() =>
        Assert.Multiple(() =>
        {
            Assert.That(GloasTiming.DeadlineMs(GloasTiming.AttestationDueBpsGloas), Is.LessThan(GloasTiming.DeadlineMs(GloasTiming.AttestationDueBps)));
            Assert.That(GloasTiming.DeadlineMs(GloasTiming.AggregateDueBpsGloas), Is.LessThan(GloasTiming.DeadlineMs(GloasTiming.AggregateDueBps)));
        });

    [Test]
    public void Payload_deadlines_fall_at_or_after_the_gloas_attestation_deadlines_but_within_the_slot() =>
        Assert.Multiple(() =>
        {
            // PAYLOAD_DUE_BPS and AGGREGATE_DUE_BPS_GLOAS are both 50% of the slot: the envelope and
            // the aggregate share a deadline, they do not race each other.
            Assert.That(GloasTiming.DeadlineMs(GloasTiming.PayloadDueBps), Is.GreaterThanOrEqualTo(GloasTiming.DeadlineMs(GloasTiming.AggregateDueBpsGloas)));
            Assert.That(GloasTiming.DeadlineMs(GloasTiming.PayloadAttestationDueBps), Is.GreaterThan(GloasTiming.DeadlineMs(GloasTiming.PayloadDueBps)),
                "the PTC vote deadline must fall strictly after the envelope deadline it votes on");
            Assert.That(GloasTiming.DeadlineMs(GloasTiming.PayloadAttestationDueBps), Is.LessThanOrEqualTo(GloasTiming.SlotDurationMs));
        });
}
