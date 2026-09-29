// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// specs/gloas/fork-choice.md <c>store.payload_timeliness_vote</c>, <c>store.payload_data_availability_vote</c>,
/// <c>on_payload_attestation_message</c> and <c>notify_ptc_messages</c> through <see cref="ForkChoiceRunner"/>.
/// </summary>
[HardTimeout(60_000)]
public class ForkChoiceRunnerPtcVoteTests
{
    /// <summary>
    /// specs/gloas/fork-choice.md <c>payload_timeliness</c> and <c>payload_data_availability</c>: strictly more than
    /// <c>PTC_SIZE // 2</c> seats must vote the value, so an even split never decides it, and seats that did not vote or voted
    /// the other way do not count.
    /// </summary>
    [Test]
    public void A_payload_decision_needs_strictly_more_than_half_the_ptc([Values(256, 257)] int matching, [Values] bool restVotedOtherwise)
    {
        bool?[] votes = new bool?[Presets.PtcSize];
        for (int seat = 0; seat < votes.Length; seat++)
            votes[seat] = seat < matching ? true : restVotedOtherwise ? false : null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PtcVotes.HasQuorum(votes, true), Is.EqualTo(matching > 256));
            Assert.That(PtcVotes.HasQuorum(votes, false), Is.False);
        }
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>on_payload_attestation_message</c>: a validator can hold several PTC seats, and its vote is
    /// written at every one, the timeliness and availability votes each from their own field. A single seat would undercount it.
    /// A correctly signed wire vote passes <c>is_valid_indexed_payload_attestation</c>.
    /// </summary>
    [Test]
    public void A_member_vote_is_written_at_every_seat_it_holds()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot);
        harness.Import(first);
        ulong[] ptc = harness.Ptc(first);
        ulong member = ptc.GroupBy(static v => v).OrderByDescending(static g => g.Count()).First().Key;
        int[] seats = [.. Enumerable.Range(0, ptc.Length).Where(seat => ptc[seat] == member)];
        Assert.That(seats, Has.Length.GreaterThan(1), "fixture bug: the member must hold several seats");

        harness.Runner.OnPayloadAttestationMessage(harness.PtcMessage(first, member, payloadPresent: true, blobDataAvailable: false, sign: true));

        (IReadOnlyList<bool?> timeliness, IReadOnlyList<bool?> availability) = harness.Runner.GetPtcVotes(first.Root)!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enumerable.Range(0, ptc.Length).Where(seat => timeliness[seat] is not null), Is.EqualTo(seats));
            Assert.That(seats.Select(seat => (timeliness[seat], availability[seat])), Is.All.EqualTo(((bool?)true, (bool?)false)));
            Assert.That(availability.Count(static v => v is not null), Is.EqualTo(seats.Length));
        }
    }

    /// <summary>specs/gloas/fork-choice.md <c>on_payload_attestation_message</c>: a vote whose slot is not its block's returns early, before the current-slot check, and writes nothing.</summary>
    [Test]
    public void A_vote_for_another_slot_than_its_block_is_ignored()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 1);
        harness.Import(first);

        harness.Runner.OnPayloadAttestationMessage(harness.PtcMessage(first, harness.Ptc(first)[0], true, true, sign: false, slot: first.Slot + 1), verifySignature: false);

        Assert.That(harness.Runner.GetPtcVotes(first.Root)!.Value.Timeliness, Is.All.Null);
    }

    public enum RefusedVote { NotInPtc, WireVoteNotForTheCurrentSlot, BadSignature, UnknownBlock, PreGloasBlock, MissingData, MissingBlockRoot }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>on_payload_attestation_message</c>: the block must be known and a Gloas block (<c>get_ptc</c>
    /// asserts a Gloas epoch), the voter a PTC member, and a wire vote for the current slot with a valid signature. A refused
    /// vote writes nothing; a vote from a block skips only the slot and signature checks.
    /// </summary>
    [Test]
    public void Refused_votes_write_nothing_and_block_votes_skip_only_slot_and_signature([Values] RefusedVote refusal, [Values] bool isFromBlock)
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(refusal == RefusedVote.WireVoteNotForTheCurrentSlot ? first.Slot + 1 : first.Slot);
        harness.Import(first);
        ulong[] ptc = harness.Ptc(first);
        ulong outsider = Enumerable.Range(0, ValidatorCount).Select(static i => (ulong)i).First(i => !ptc.Contains(i));

        PayloadAttestationMessage message = harness.PtcMessage(first, refusal == RefusedVote.NotInPtc ? outsider : ptc[0], true, true, sign: true);
        switch (refusal)
        {
            case RefusedVote.BadSignature:
                message.Signature = Corrupt(message.Signature);
                break;
            case RefusedVote.UnknownBlock:
                message.Data!.BeaconBlockRoot = Hash(0xEE);
                break;
            case RefusedVote.PreGloasBlock:
                message.Data!.BeaconBlockRoot = harness.Chain.AnchorRoot;
                message.Data.Slot = 0;
                break;
            case RefusedVote.MissingData:
                message.Data = null;
                break;
            case RefusedVote.MissingBlockRoot:
                message.Data!.BeaconBlockRoot = null;
                break;
        }

        bool refused = !isFromBlock || refusal is RefusedVote.NotInPtc or RefusedVote.UnknownBlock or RefusedVote.PreGloasBlock or RefusedVote.MissingData or RefusedVote.MissingBlockRoot;
        Assert.That(() => harness.Runner.OnPayloadAttestationMessage(message, isFromBlock), refused ? Throws.TypeOf<ForkChoiceException>() : Throws.Nothing);
        Assert.That(harness.Runner.GetPtcVotes(first.Root)!.Value.Timeliness.Count(static v => v is not null), refused ? Is.Zero : Is.GreaterThan(0));
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>get_forkchoice_store</c> (#5545): a Gloas anchor starts with <c>[None] * PTC_SIZE</c> votes,
    /// so a PTC vote for it right after startup is recorded, not refused for a missing entry.
    /// </summary>
    [Test]
    public void A_gloas_anchor_takes_ptc_votes_right_after_startup()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        PubkeyCache pubkeys = new();
        pubkeys.Build(chain.First.PostState.Validators!);
        ForkChoiceRunner runner = new(chain.Spec, chain.First.PostState, chain.First.Block.Message!, chain, pubkeys, chain);
        GloasForkChoiceHarness harness = new();
        ulong member = harness.Ptc(harness.First)[0];

        runner.OnPayloadAttestationMessage(harness.PtcMessage(harness.First, member, true, true, sign: true));

        Assert.That(runner.GetPtcVotes(chain.First.Root)!.Value.Timeliness, Has.Some.True);
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>on_block</c> calls <c>notify_ptc_messages</c>: a block's payload attestations vote for its
    /// parent's payload from inside <see cref="ForkChoiceRunner.OnBlock(SignedBeaconBlockGloas, BeaconStateGloas)"/>, at every seat
    /// of each attester, with no caller replay.
    /// </summary>
    [Test]
    public void A_block_payload_attestations_are_applied_by_on_block()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 1);
        harness.Import(first);
        ulong[] ptc = harness.Ptc(first);
        PayloadAttestation vote = PtcAttestation(first.PostState, VoteData(first, payloadPresent: true), [0], sign: false);

        harness.Import(harness.Child(first, first.Slot + 1, full: false, 0xC1, vote));

        IReadOnlyList<bool?> timeliness = harness.Runner.GetPtcVotes(first.Root)!.Value.Timeliness;
        Assert.That(Enumerable.Range(0, ptc.Length).Where(seat => timeliness[seat] is true), Is.EqualTo(Enumerable.Range(0, ptc.Length).Where(seat => ptc[seat] == ptc[0])));
    }

    /// <summary>
    /// Every body payload attestation is resolved before the block is registered, so a block refused for one of them leaves
    /// neither itself nor the votes of the others behind; a partial write would count votes of a block fork choice never held.
    /// </summary>
    [Test]
    public void A_block_refused_for_one_payload_attestation_writes_none_of_them()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 1);
        harness.Import(first);
        PayloadAttestation valid = PtcAttestation(first.PostState, VoteData(first, payloadPresent: true), [0], sign: false);
        PayloadAttestationData unknownBlock = VoteData(first, payloadPresent: true);
        unknownBlock.BeaconBlockRoot = Hash(0xEE);
        GloasForkChoiceHarness.Block child = harness.Child(first, first.Slot + 1, full: false, 0xC1, valid, PtcAttestation(first.PostState, unknownBlock, [1], sign: false));

        Assert.That(() => harness.Import(child), Throws.TypeOf<ForkChoiceException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Runner.ContainsBlock(child.Root), Is.False);
            Assert.That(harness.Runner.GetPtcVotes(first.Root)!.Value.Timeliness, Is.All.Null);
        }
    }

    private static PayloadAttestationData VoteData(GloasForkChoiceHarness.Block block, bool payloadPresent) =>
        new() { BeaconBlockRoot = block.Root, Slot = block.Slot, PayloadPresent = payloadPresent, BlobDataAvailable = true };
}
