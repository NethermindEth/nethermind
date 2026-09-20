// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// Real state transitions through the Gloas block-processing pipeline (<see cref="GloasBlockProcessing"/>):
/// the bid/envelope split, the binding of envelope verification to the named block's frozen
/// post-state, withdrawals computed from state, and the builder-payment machinery for a payload
/// settled within the epoch it was committed in. Fixtures come from <see cref="GloasTestFixtures"/>.
/// </summary>
public class GloasBlockProcessingTests
{
    // ---- Bid processing: signature verification actually gates state mutation ----

    [Test]
    public void ProcessExecutionPayloadBid_rejects_a_bid_whose_signature_does_not_match_the_registered_builder()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 5 * Gwei);
        // Flip a byte of the otherwise-valid signature: still 96 well-formed bytes, just the wrong one.
        byte[] corrupted = (byte[])bid.Signature.Bytes.ToArray().Clone();
        corrupted[10] ^= 0xFF;
        bid.Signature = new BlsSignature(corrupted);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true))!;

        Assert.That(ex.Message, Does.Contain("Invalid execution payload bid signature"));
        Assert.That(state.LatestExecutionPayloadBid!.BuilderIndex, Is.EqualTo(Presets.BuilderIndexSelfBuild),
            "a rejected bid must never be committed to state");
    }

    [Test]
    public void ProcessExecutionPayloadBid_accepts_a_correctly_signed_builder_bid_and_records_its_pending_payment()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        const ulong value = 7 * Gwei;
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: value);

        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);

        Assert.Multiple(() =>
        {
            Assert.That(state.LatestExecutionPayloadBid, Is.SameAs(bid.Message));
            BuilderPendingPayment payment = state.BuilderPendingPayments![(int)(Presets.SlotsPerEpoch + bid.Message!.Slot % Presets.SlotsPerEpoch)];
            Assert.That(payment.Withdrawal!.Amount, Is.EqualTo(value));
            Assert.That(payment.Withdrawal.BuilderIndex, Is.EqualTo(0ul));
            Assert.That(payment.Withdrawal.FeeRecipient, Is.EqualTo(bid.Message.FeeRecipient));
        });
    }

    [Test]
    public void ProcessExecutionPayloadBid_rejects_a_builder_who_cannot_cover_the_bid()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        // The builder's whole balance is 40 Gwei (see CreateGloasState); a bid above balance minus
        // MIN_DEPOSIT_AMOUNT must be rejected rather than driving the builder's balance negative.
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 100 * Gwei);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true))!;
        Assert.That(ex.Message, Does.Contain("cannot cover a bid"));
    }

    // ---- Envelope verification: a pure check against the committed bid, no state mutation ----

    [Test]
    public void VerifyExecutionPayloadEnvelope_rejects_an_envelope_whose_payload_does_not_match_the_committed_bid()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);

        // Sign a self-consistent envelope built against a gas limit the actually-committed bid never
        // used - the signature is genuinely valid (it covers exactly this envelope's own content),
        // so this exercises the cross-check against state.latest_execution_payload_bid, not signing.
        ExecutionPayloadBid differentGasLimit = WithGasLimit(bid.Message!, bid.Message!.GasLimit + 1);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, differentGasLimit, builderSk, builderIndex: 0);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new BlockStates().Add(state), envelope, new AcceptingNotifier(), new PubkeyCache()))!;
        Assert.That(ex.Message, Does.Contain("does not match the committed bid"));
    }

    [Test]
    public void VerifyExecutionPayloadEnvelope_accepts_a_matching_envelope_and_mutates_nothing()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        Hash256 latestBlockHashBefore = state.LatestBlockHash!;
        Hash256 rootBefore = SszRoots.HashTreeRoot(state);

        Assert.DoesNotThrow(() => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new BlockStates().Add(state), envelope, new AcceptingNotifier(), new PubkeyCache()));

        Assert.That(state.LatestBlockHash, Is.EqualTo(latestBlockHashBefore), "verification must not apply the payload - that happens one block later");
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(rootBefore));
    }

    // ---- Envelope verification is bound to the frozen post-state of the block the envelope names ----

    /// <summary>
    /// The reproduction of the defect the binding exists for. Two states that differ only in the bid
    /// they committed; the second is the post-state of a block that was never produced. An envelope
    /// built against that second state is self-consistent, so the spec's per-field checks accept it -
    /// which is exactly what the first assertion pins. Only resolving the state by the envelope's own
    /// block root, against the set of blocks that really exist, rejects it.
    /// </summary>
    [Test]
    public void VerifyExecutionPayloadEnvelope_rejects_an_envelope_for_a_block_whose_post_state_is_not_known_even_though_the_envelope_is_self_consistent()
    {
        BeaconStateGloas real = CreateGloasState(out Bls.SecretKey builderSk, out _);
        BeaconStateGloas fabricated = CreateGloasState(out _, out _);
        GloasBlockProcessing.ProcessExecutionPayloadBid(real, ValidBuilderBid(real, builderSk, builderIndex: 0, value: 3 * Gwei), SyntheticSpec(), new PubkeyCache(), verifySignature: true);
        SignedExecutionPayloadBid otherBid = ValidBuilderBid(fabricated, builderSk, builderIndex: 0, value: 5 * Gwei, blockHashFill: 0x89);
        GloasBlockProcessing.ProcessExecutionPayloadBid(fabricated, otherBid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);
        Assert.That(BlockRootOf(fabricated), Is.Not.EqualTo(BlockRootOf(real)), "fixture bug: the two states must be the post-states of different blocks");

        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(fabricated, otherBid.Message!, builderSk, builderIndex: 0);

        // The spec's verify_execution_payload_envelope(state, ...) body, handed the caller's state
        // directly: every field agrees with the state it is compared against, so it passes.
        MethodInfo unbound = typeof(GloasBlockProcessing).GetMethod("VerifyExecutionPayloadEnvelopeAgainst", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("the per-field verification body was renamed; update this test");
        Assert.That(() => unbound.Invoke(null, [fabricated, envelope, new AcceptingNotifier(), new PubkeyCache()]), Throws.Nothing,
            "the per-field checks alone cannot tell a fabricated post-state from a real one; if this starts failing the binding below may have become redundant");

        // The bound entry point knows which blocks exist and refuses the one that does not.
        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new BlockStates().Add(real), envelope, new AcceptingNotifier(), new PubkeyCache()))!;
        Assert.That(ex.Message, Does.Contain("post-state is not known").And.Contain(BlockRootOf(fabricated).ToString()));
    }

    [Test]
    public void VerifyExecutionPayloadEnvelope_uses_the_frozen_post_state_of_the_named_block_not_the_lineage_that_has_since_moved_on()
    {
        // Two equal copies of the same lineage: one is frozen as block 1's post-state, the other
        // keeps advancing. Building the copy twice stands in for the clone the importer retains.
        BeaconStateGloas frozen = CreateGloasState(out Bls.SecretKey builderSk, out _);
        BeaconStateGloas live = CreateGloasState(out _, out _);
        EpochCache cache = new();
        SignedExecutionPayloadBid bid1 = ValidBuilderBid(frozen, builderSk, builderIndex: 0, value: 4 * Gwei);
        ApplyBlock(frozen, MinimalBlock(frozen, bid1), cache);
        ApplyBlock(live, MinimalBlock(live, ValidBuilderBid(live, builderSk, builderIndex: 0, value: 4 * Gwei)), new EpochCache());
        Hash256 block1Root = BlockRootOf(frozen);
        Assert.That(BlockRootOf(live), Is.EqualTo(block1Root), "fixture bug: the two copies must agree on block 1's root before the lineage moves on");
        SignedExecutionPayloadEnvelope envelope1 = ValidEnvelope(frozen, bid1.Message!, builderSk, builderIndex: 0);

        // The lineage moves on: block 2 commits a different bid and the state now describes a
        // different latest block, so it is no longer a state envelope1 can be verified against.
        live.Slot++;
        ApplyBlock(live, MinimalBlock(live, SelfBuildBid(live, parentBlockHash: bid1.Message!.BlockHash!, blockHash: Hash(0x99))), new EpochCache());
        Assert.That(BlockRootOf(live), Is.Not.EqualTo(block1Root));

        BlockStates states = new BlockStates().Add(frozen).Add(live);
        Assert.DoesNotThrow(() => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(states, envelope1, new AcceptingNotifier(), new PubkeyCache()),
            "block 1's envelope must verify against block 1's frozen post-state however far the lineage has advanced");

        // Had the provider handed back the advanced state under block 1's root, the spec's own
        // block-root consistency check is what catches the mismatch - the second line of defense.
        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new MisboundStates(block1Root, live), envelope1, new AcceptingNotifier(), new PubkeyCache()))!;
        // The parent-root check one line later fails too and its message also says "beacon block
        // root does not match"; only the block-root check names the state's own latest block.
        Assert.That(ex.Message, Does.Contain("does not match the state's own latest block"));
    }

    [Test]
    public void PostStateCache_serves_retained_gloas_states_by_block_root_and_refuses_unknown_roots()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);

        PostStateCache cache = new(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), SyntheticSpec(), Hash(0x01), new BeaconStateFulu());
        Assert.That(cache.GetGloasBlockState(BlockRootOf(state)), Is.Null, "nothing retained yet");
        Assert.That(() => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(cache, envelope, new AcceptingNotifier(), new PubkeyCache()),
            Throws.TypeOf<BeaconStateException>().With.Message.Contains("post-state is not known"));

        cache.RetainGloas(BlockRootOf(state), state);

        Assert.That(cache.GetGloasBlockState(BlockRootOf(state)), Is.SameAs(state));
        Assert.That(cache.GetGloasBlockState(Hash(0x77)), Is.Null, "a root that was never retained must not resolve to any state");
        Assert.DoesNotThrow(() => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(cache, envelope, new AcceptingNotifier(), new PubkeyCache()));
    }

    // ---- The two-step apply: bid committed in one block, applied and paid out in the next ----

    [Test]
    public void The_two_step_apply_settles_and_pays_the_builder_exactly_one_block_after_the_bid_was_committed()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out ulong builderStartingBalance);
        const ulong bidValue = 9 * Gwei;
        EpochCache cache = new();

        Hash256 rootBeforeBlock1 = SszRoots.HashTreeRoot(state);

        // Block 1: the builder's bid is committed (a pending payment is recorded, nothing paid yet).
        SignedExecutionPayloadBid bid1 = ValidBuilderBid(state, builderSk, builderIndex: 0, value: bidValue);
        SignedBeaconBlockGloas block1 = MinimalBlock(state, bid1);
        ApplyBlock(state, block1, cache);

        Assert.That(state.Builders![0].Balance, Is.EqualTo(builderStartingBalance), "committing a bid must not yet move any balance");
        ulong paymentIndex = Presets.SlotsPerEpoch + block1.Message!.Slot % Presets.SlotsPerEpoch;
        Assert.That(state.BuilderPendingPayments![(int)paymentIndex].Withdrawal!.Amount, Is.EqualTo(bidValue));

        // The hash tree root is not just a stand-in for "some field changed": it must actually be a
        // pure, deterministic function of the mutated state (two independent recomputations agree),
        // and it must differ once real mutations (the committed bid, the new header, RANDAO, the
        // sync aggregate's balance changes) have actually landed.
        Hash256 rootAfterBlock1First = SszRoots.HashTreeRoot(state);
        Hash256 rootAfterBlock1Second = SszRoots.HashTreeRoot(state);
        Assert.That(rootAfterBlock1First, Is.EqualTo(rootAfterBlock1Second), "hash_tree_root must be a pure function of the state, not vary call to call");
        Assert.That(rootAfterBlock1First, Is.Not.EqualTo(rootBeforeBlock1), "processing block 1 must actually have mutated the state");

        // The envelope for block 1 is independently verified against block 1's frozen post-state - a
        // pure check, exercised for its own sake (see the tests above); the payload is applied one block later.
        SignedExecutionPayloadEnvelope envelope1 = ValidEnvelope(state, bid1.Message!, builderSk, builderIndex: 0);
        Assert.DoesNotThrow(() => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new BlockStates().Add(state), envelope1, new AcceptingNotifier(), new PubkeyCache()));

        // Block 2: its own bid declares bid1's payload delivered (parent_block_hash == bid1.block_hash)
        // with empty parent execution requests, matching the empty root bid1 committed to.
        state.Slot++; // advance one slot inside the same epoch (GloasSlotProcessing would also do this; skipped here to keep the fixture to the two blocks under test)
        SignedExecutionPayloadBid bid2 = SelfBuildBid(state, parentBlockHash: bid1.Message!.BlockHash!, blockHash: Hash(0x99));
        SignedBeaconBlockGloas block2 = MinimalBlock(state, bid2);
        ApplyBlock(state, block2, cache);

        Hash256 rootAfterBlock2 = SszRoots.HashTreeRoot(state);
        Assert.Multiple(() =>
        {
            Assert.That(state.LatestBlockHash, Is.EqualTo(bid1.Message.BlockHash), "the parent payload must be applied exactly one block later");
            Assert.That(state.BuilderPendingPayments[(int)paymentIndex].Withdrawal!.Amount, Is.EqualTo(0ul), "the settled payment slot must be cleared");
            Assert.That(state.BuilderPendingWithdrawals, Is.Empty, "the settled withdrawal must have been paid out by this same block's own withdrawals step");
            Assert.That(state.Builders![0].Balance, Is.EqualTo(builderStartingBalance - bidValue), "the builder must actually have been paid the bid value");
            Assert.That(rootAfterBlock2, Is.EqualTo(SszRoots.HashTreeRoot(state)), "hash_tree_root must still be stable after the second block");
            Assert.That(rootAfterBlock2, Is.Not.EqualTo(rootAfterBlock1First), "settling and paying out the builder must actually change the state root");
        });
    }

    // ---- Withdrawals computed from state alone ----

    [Test]
    public void ProcessWithdrawals_pays_a_fully_withdrawable_validator_computed_entirely_from_state()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        // Make the parent payload "delivered" so process_withdrawals does not take its early-return path.
        state.LatestBlockHash = state.LatestExecutionPayloadBid!.BlockHash;

        int validatorIndex = 2;
        const ulong startingBalance = 40 * Gwei;
        Validator validator = state.Validators![validatorIndex].Clone();
        validator.WithdrawalCredentials = EthWithdrawalCredentials(0xAB);
        validator.WithdrawableEpoch = 0;
        state.Validators[validatorIndex] = validator;
        state.Balances![validatorIndex] = startingBalance;

        GloasBlockProcessing.ProcessWithdrawals(state);

        Assert.Multiple(() =>
        {
            Assert.That(state.Balances[validatorIndex], Is.EqualTo(0ul), "a fully withdrawable validator's whole balance must be swept");
            Assert.That(state.PayloadExpectedWithdrawals, Has.Length.EqualTo(1));
            Withdrawal withdrawal = state.PayloadExpectedWithdrawals![0];
            Assert.That(withdrawal.ValidatorIndex, Is.EqualTo((ulong)validatorIndex));
            Assert.That(withdrawal.Amount, Is.EqualTo(startingBalance));
            Assert.That(withdrawal.Address, Is.EqualTo(new Address(validator.WithdrawalCredentials.Bytes[12..])));
        });
    }

    [Test]
    public void ProcessWithdrawals_is_a_no_op_when_the_parent_payload_was_not_delivered()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        // LatestBlockHash left at its post-upgrade value, which never equals the committed bid's
        // block hash unless a parent payload was actually applied (see ApplyParentExecutionPayload).
        ulong balanceBefore = state.Balances![2];

        GloasBlockProcessing.ProcessWithdrawals(state);

        Assert.That(state.Balances[2], Is.EqualTo(balanceBefore));
        Assert.That(state.PayloadExpectedWithdrawals, Is.Empty.Or.Null);
    }

    // ---- Declared, by-name gaps must fail loudly rather than silently mishandle the block ----

    [Test]
    public void ProcessOperations_throws_by_name_for_an_attester_slashing_it_does_not_process()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        BeaconBlockBodyGloas body = new()
        {
            Deposits = [],
            AttesterSlashings = [new AttesterSlashingGloas { Attestation1 = new IndexedAttestationGloas(), Attestation2 = new IndexedAttestationGloas() }],
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => GloasBlockProcessing.ProcessOperations(state, body, parentSlot: 0))!;
        Assert.That(ex.Message, Does.Contain("attester slashings"));
    }

    private static ExecutionPayloadBid WithGasLimit(ExecutionPayloadBid bid, ulong gasLimit) => new()
    {
        ParentBlockHash = bid.ParentBlockHash,
        ParentBlockRoot = bid.ParentBlockRoot,
        BlockHash = bid.BlockHash,
        PrevRandao = bid.PrevRandao,
        FeeRecipient = bid.FeeRecipient,
        GasLimit = gasLimit,
        BuilderIndex = bid.BuilderIndex,
        Slot = bid.Slot,
        Value = bid.Value,
        BlobKzgCommitments = bid.BlobKzgCommitments,
        ExecutionRequestsRoot = bid.ExecutionRequestsRoot,
    };

    /// <summary>A provider with a bug: it answers <paramref name="root"/> with a state that is not that block's post-state.</summary>
    private sealed class MisboundStates(Hash256 root, BeaconStateGloas state) : IGloasBlockStateProvider
    {
        public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot) => blockRoot == root ? state : null;
    }
}
