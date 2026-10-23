// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class InclusionListValidatorTests
{
    private static readonly ISpecProvider _specProvider = new CustomSpecProvider(((ForkActivation)0, Bogota.Instance));
    // Bogota carries inclusion lists alone; Profile 2 is active once frame transactions, keyed nonces and
    // recent roots are scheduled alongside them, as Eip8369Prototype does.
    private static readonly ISpecProvider _frameSpecProvider = new CustomSpecProvider(((ForkActivation)0, Eip8369Prototype.Instance));
    // Inclusion lists and frame transactions without EIP-8250 and EIP-8272: Profile 2 is not yet active.
    private static readonly IReleaseSpec _preProfile2Spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip8141Enabled = true };
    private static readonly TxValidator _txValidator = new(TestBlockchainIds.ChainId);
    private static readonly Transaction _validTx = BuildTx();

    public static IEnumerable<TestCaseData> SatisfactionCases
    {
        get
        {
            static TestCaseData Case(string name, Transaction[] il, bool satisfied, Transaction[]? blockTxs = null, ulong gasUsed = 1_000_000, UInt256 baseFee = default, ulong senderNonce = 0) =>
                new(blockTxs ?? [], il, gasUsed, baseFee, senderNonce, satisfied) { TestName = name };

            yield return Case("Full block is satisfied", [_validTx], true, gasUsed: 30_000_000);
            yield return Case("All IL txs included", [_validTx], true, blockTxs: [_validTx]);
            yield return Case("Appendable IL tx excluded", [_validTx], false);
            yield return Case("Empty IL", [], true);
            yield return Case("Sender lacks balance", [BuildTx(value: 100.Ether, to: TestItem.AddressB)], true);
            yield return Case("Wrong nonce", [BuildTx(nonce: 5, to: TestItem.AddressB)], true);
            yield return Case("Gas price below base fee", [BuildTx(gasPrice: 1.GWei, to: TestItem.AddressB)], true, baseFee: 5.GWei);
            yield return Case("Gas limit exceeds remaining block gas", [BuildTx(gasLimit: 25_000_000, to: TestItem.AddressB)], true, gasUsed: 10_000_000);
            // Regression: block.GasLimit - tx.GasLimit would underflow and mark the block unsatisfied.
            yield return Case("Gas limit exceeds block gas limit", [BuildTx(gasLimit: 100_000_000, to: TestItem.AddressB)], true);
            // An included tx and a not-appendable (wrong nonce) tx both absolve the builder.
            yield return Case("Partially included, remainder invalid", [_validTx, BuildTx(nonce: 7, value: UInt256.One, to: TestItem.AddressB)], true, blockTxs: [_validTx]);
            // A same-nonce replacement advances the sender nonce, so the IL tx is no longer appendable.
            yield return Case("Same-nonce replacement advances nonce", [_validTx], true, blockTxs: [BuildTx(value: UInt256.One, to: TestItem.AddressC)], senderNonce: 1);
            // EIP-1559 fee check uses MaxFeePerGas (cap), not the tip: cap above baseFee → appendable.
            yield return Case("EIP-1559 low tip but sufficient fee cap", [Build1559Tx()], false, baseFee: 5.GWei);
            yield return Case("Blob tx is ignored", [BuildBlobTx()], true);
            yield return Case("Blob entry does not hide an omitted ordinary tx", [BuildBlobTx(), _validTx], false);
            yield return Case("Blob entry is ignored after an included ordinary tx", [_validTx, BuildBlobTx()], true, blockTxs: [_validTx]);
            // A tx normal execution rejects must not be reported appendable.
            yield return Case("Malformed 1559 tx (tip > fee cap)", [BuildMalformed1559Tx()], true);
            // A tx whose GasLimit is below the intrinsic cost cannot execute.
            // Non-self recipient so the full 21_000 floor applies rather than the EIP-2780 self-transfer cost.
            yield return Case("Intrinsic gas too low", [BuildTx(gasLimit: 20_999, to: TestItem.AddressB)], true);
            // A data-free self-transfer costs 12000 intrinsic (EIP-2780), so the 21000-gas full-block
            // shortcut must not report "satisfied".
            yield return Case("Self-transfer fits under EIP-2780 12000 base", [BuildTx(gasLimit: 15_000, to: TestItem.AddressA)], false, gasUsed: 29_985_000);
            // 65536 * 2^240 wraps UInt256 to 0, faking an affordable cost; the overflow-checked path rejects it.
            yield return Case("Tx cost overflows 256 bits", [BuildTx(gasLimit: 65_536, gasPrice: new UInt256(0, 0, 0, 1UL << 48), value: UInt256.One, to: TestItem.AddressB)], true);
            // The spec disallows duplicates, but adversarial input must not cause false rejection.
            yield return Case("Duplicate IL entries with tx included", [_validTx, _validTx], true, blockTxs: [_validTx], senderNonce: 1);
        }
    }

    /// <summary>Before Profile 2 is active an omitted frame transaction is excused, as before this change: committee
    /// positions and claims have no effect, and nothing is replayed or signature-checked.</summary>
    [TestCase(10_000_000_000_000_000_000UL, 0UL, TestName = "Before Profile 2 an omitted frame tx is excused despite a claim")]
    [TestCase(1UL, 1UL, TestName = "Before Profile 2 an omitted frame tx is excused whatever its sender's state")]
    public void Frame_tx_omission_is_excused_before_profile_2(ulong senderBalance, ulong senderNonce)
    {
        // Without EIP-8250 a frame transaction still carries the scalar account nonce.
        Transaction candidate = Candidate(1, 100_000);
        candidate.NonceKeys = null;
        Profile2Case profile2 = Profile2Block([candidate], blockTxCount: 3, claims: [new(candidate.Hash!, 1)]);
        FakeReplayer replayer = new((_, _) => true);

        bool result = InclusionListValidator.IsSatisfied(profile2.Block, profile2.Lists!, StateWith(TestItem.AddressA, senderBalance, senderNonce),
            _preProfile2Spec, _txValidator, Eip8369Constants.MaxVerifyGasPerTx, profile2.Claims, replayer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8369Profile2.IsActive(_preProfile2Spec), Is.False);
            Assert.That(result, Is.True);
            Assert.That(replayer.ReplayedAt, Is.Empty);
            Assert.That(replayer.SignatureChecks, Is.Zero);
        }
    }

    /// <summary>EIP-7805 excludes blob transactions from the appendability check, so their omission is excused
    /// whether or not Profile 2 is active.</summary>
    [TestCase(false, true, TestName = "Omitted blob tx is excused before Profile 2 is active")]
    [TestCase(true, true, TestName = "Omitted blob tx is excused once Profile 2 is active")]
    public void Blob_tx_omission_is_excused_once_profile_2_is_active(bool profile2Active, bool satisfied)
    {
        IReleaseSpec spec = profile2Active ? Eip8369Prototype.Instance : Bogota.Instance;
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .TestObject;

        Assert.That(IsSatisfied(block, [BuildBlobTx()], StateWith(TestItem.AddressA, 10.Ether, 0), spec), Is.EqualTo(satisfied));
    }

    [TestCaseSource(nameof(SatisfactionCases))]
    public void Inclusion_list_satisfaction(Transaction[] blockTxs, Transaction[] il, ulong gasUsed, UInt256 baseFee, ulong senderNonce, bool satisfied)
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(gasUsed)
            .WithBaseFeePerGas(baseFee)
            .WithTransactions(blockTxs)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .TestObject;

        IReadOnlyStateProvider state = StateWith(TestItem.AddressA, 10.Ether, senderNonce);
        Assert.That(IsSatisfied(block, il, state, _specProvider.GetSpec(block.Header)), Is.EqualTo(satisfied));
    }

    // EIP-8037 admits a transaction per dimension, so an entry that fits the state gas the block actually spent
    // is appendable even though it exceeds the header's max(execution, state). Numbers are those of
    // test_preparation_rollback_restores_block_state_budget, where a 97_920-gas state total sits under a
    // 513_317-gas execution total.
    [TestCase(34_067_749UL, true, ExpectedResult = false, TestName = "Entry at the exact state budget is appendable")]
    [TestCase(34_067_750UL, true, ExpectedResult = true, TestName = "Entry one gas past the state budget is not appendable")]
    // Without the per-dimension totals — no in-memory copy of the executed block left — both fall back to that max.
    [TestCase(34_067_749UL, false, ExpectedResult = true, TestName = "Entry is judged on the combined gas when dimensions are unknown")]
    public bool Appendability_is_judged_per_block_gas_dimension(ulong ilGasLimit, bool dimensionsKnown)
    {
        Block block = Build.A.Block
            .WithGasLimit(34_165_669)
            .WithGasUsed(513_317)
            .WithBaseFeePerGas(UInt256.Zero)
            .WithTransactions([])
            .TestObject;
        if (dimensionsKnown) block.Header.GasUsedPerDimension = (513_317, 97_920);

        return IsSatisfied(block, [BuildTx(gasLimit: ilGasLimit, to: TestItem.AddressB)], StateWith(TestItem.AddressA, 10.Ether, 0), _specProvider.GetSpec(block.Header));
    }

    /// <summary>EIP-8369 Profile 2: an omitted frame transaction is judged when it is a candidate and excused
    /// when it is not, so both sides of the boundary are pinned rather than only the excuse.</summary>
    public static IEnumerable<TestCaseData> FrameCases
    {
        get
        {
            static TestCaseData Case(string name, Transaction[] il, bool satisfied, Transaction[]? blockTxs = null,
                UInt256 payerBalance = default, UInt256 senderBalance = default, ulong gasUsed = 1_000_000,
                UInt256 baseFee = default, ulong maxVerifyGasPerTx = Eip8369Constants.MaxVerifyGasPerTx,
                bool wellFormed = true) =>
                new(il, blockTxs ?? [], senderBalance.IsZero ? 10.Ether : senderBalance, payerBalance, gasUsed,
                    baseFee, maxVerifyGasPerTx, wellFormed, satisfied)
                { TestName = name };

            // A Profile 2 candidate the payload could have appended is now an unjustified omission.
            yield return Case("Omitted Profile 2 candidate is censoring", [BuildFrameTx()], false);

            Transaction included = BuildFrameTx();
            included.Hash = Keccak.Zero;
            yield return Case("Included frame transaction repeated in the IL is satisfied",
                [included, included], true, blockTxs: [included]);
            Transaction omitted = BuildFrameTx(frames: [SelfVerify(110_000)]);
            omitted.Hash = Keccak.EmptyTreeHash;
            yield return Case("Included frame duplicates do not excuse a different omitted candidate",
                [included, included, omitted], false, blockTxs: [included]);

            // Outside both EIP-8369 profiles: blob gas has its own budget, over which the EIP defines no check.
            yield return Case("Omitted blob-carrying frame transaction is excused", [BuildFrameTx(blobCount: 1)], true);
            // Condition 2: a prefix matching none of the four admitted shapes.
            yield return Case("Omitted frame transaction with an unrecognized prefix is excused",
                [BuildFrameTx([Body(50_000)])], true);
            // Condition 3: a VERIFY frame behind the validation prefix.
            yield return Case("Omitted frame transaction with a VERIFY frame after its prefix is excused",
                [BuildFrameTx([SelfVerify(), Body(50_000), Verify(50_000)])], true);
            // Condition 4, and the knob that sets it: the same transaction is judged once the cap is lifted.
            yield return Case("Omitted frame transaction over the VERIFY budget is excused",
                [BuildFrameTx([SelfVerify(2_000_000)])], true);
            // Lifting the per-transaction cap cannot admit it: it still overruns MAX_VERIFY_GAS_PER_IL.
            yield return Case("Omitted frame transaction over the per-IL VERIFY budget stays excused once the per-transaction cap is lifted",
                [BuildFrameTx([SelfVerify(2_000_000)])], true, maxVerifyGasPerTx: 0);
            yield return Case("Omitted frame transaction within the per-IL VERIFY budget is judged once the per-transaction cap is lifted",
                [BuildFrameTx([SelfVerify(1_000_000)])], false, maxVerifyGasPerTx: 0);
            // Condition 1: a candidate shape is not enough, the transaction must also be statically valid.
            yield return Case("Omitted malformed frame transaction is excused",
                [BuildFrameTx([SelfVerify(value: UInt256.One)])], true, wellFormed: false);

            // A sponsored transaction is judged against the payer its prefix nominates, not against its sender.
            yield return Case("Sponsored frame transaction is judged against its payer, not its sender",
                [BuildFrameTx(SponsoredFrames)], false, payerBalance: 10.Ether, senderBalance: UInt256.One);
            // EIP-7805 collects the maximum cost in the replay, from the state at the evaluation index, so a payer
            // short of it at the end of the payload does not excuse the omission here.
            yield return Case("Sponsored frame transaction's payer balance is left to the replay",
                [BuildFrameTx(SponsoredFrames)], false, payerBalance: UInt256.One);

            // The determinate justifications EIP-8369 leaves to the attester's own reading of the payload.
            // 1,105,000 gas remains: the frame limits alone fit, and only the EIP-8141 intrinsic cost on
            // top of them — which Transaction.GasLimit leaves out — puts the reservation over.
            yield return Case("Omitted frame transaction whose max_gas does not fit the remaining gas is excused",
                [BuildFrameTx([SelfVerify(), Body(1_000_000)])], true, gasUsed: 28_895_000);
            yield return Case("Omitted frame transaction priced below the base fee is excused",
                [BuildFrameTx(maxFeePerGas: 1.GWei)], true, baseFee: 5.GWei);
        }
    }

    /// <summary>An excused entry excuses itself alone. Hoisting the excusal to the whole list would report
    /// genuine censoring as satisfied, and no case below a single-entry list can catch that.</summary>
    [TestCase(false, ExpectedResult = false, TestName = "Ordinary entry behind an excused frame transaction is still judged")]
    // Keeps the case above from passing vacuously against a validator that fails any multi-entry list.
    [TestCase(true, ExpectedResult = true, TestName = "Ordinary entry behind an excused frame transaction can satisfy the list")]
    public bool Frame_transaction_skip_is_per_entry(bool ordinaryEntryIncluded)
    {
        // A frame transaction whose prefix matches no admitted shape: outside Profile 2, so excused.
        Transaction[] il = [BuildFrameTx([Body(50_000)]), _validTx];
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions(ordinaryEntryIncluded ? [_validTx] : [])
            .TestObject;

        return IsSatisfied(block, il, StateWith(TestItem.AddressA, 10.Ether, 0), _frameSpecProvider.GetSpec(block.Header));
    }

    [TestCaseSource(nameof(FrameCases))]
    public void Frame_transaction_omission_follows_eip_8369_profile_2(
        Transaction[] il, Transaction[] blockTxs, UInt256 senderBalance, UInt256 payerBalance,
        ulong gasUsed, UInt256 baseFee, ulong maxVerifyGasPerTx, bool wellFormed, bool satisfied)
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(gasUsed)
            .WithBaseFeePerGas(baseFee)
            .WithTransactions(blockTxs)
            .TestObject;
        // Admission is by hash and by committee position, so every entry gets a hash and sits at position 0.
        for (int i = 0; i < il.Length; i++) il[i].Hash ??= Keccak.Compute($"frame-case-{i}");
        IReleaseSpec spec = _frameSpecProvider.GetSpec(block.Header);
        IReadOnlyStateProvider state = StateWith((TestItem.AddressA, senderBalance, 0), (TestItem.AddressB, payerBalance, 0));

        using (Assert.EnterMultipleScope())
        {
            // Keeps every case honest: an entry could otherwise be excused for being malformed instead.
            foreach (Transaction tx in il)
                if (tx.SupportsFrames)
                    Assert.That((bool)_txValidator.IsWellFormed(tx, spec, block.GasLimit), Is.EqualTo(wellFormed));

            Assert.That(InclusionListValidator.IsSatisfied(block, AtPositions(il), state, spec, _txValidator, maxVerifyGasPerTx, claims: null), Is.EqualTo(satisfied));
        }
    }

    [TestCase(0UL, ExpectedResult = false)]
    [TestCase(1UL, ExpectedResult = true)]
    public bool Profile_2_appendability_uses_the_exact_state_reservation(ulong excess)
    {
        Transaction candidate = WithHash(BuildFrameTx([
            SelfVerify(),
            new(FrameMode.Default, FrameFlags.None, TestItem.AddressC, 50_000, 200_000, UInt256.Zero, default),
        ]), "state-reservation");
        Profile2Case profile2 = Profile2Block([candidate], blockTxCount: 0);
        Block block = profile2.Block;
        IReleaseSpec spec = _frameSpecProvider.GetSpec(block.Header);
        Assert.That(FrameTxValidation.TryCalculateBlockGasReservations(candidate, spec, out _, out ulong stateReservation), Is.True);
        ulong stateUsed = block.GasLimit - stateReservation + excess;
        block.Header.GasUsed = stateUsed;
        block.Header.GasUsedPerDimension = (28_000_000, stateUsed);
        Assert.That((bool)_txValidator.IsWellFormed(candidate, spec, block.GasLimit), Is.True);

        return IsSatisfied(profile2, new FakeReplayer((_, _) => true));
    }

    private static readonly Transaction _candidate = Candidate(1, 100_000);

    /// <summary>EIP-7805 § Evaluation index: the one index an omitted Profile 2 candidate is replayed at, and how a
    /// claim moves it. Each case names the indices the candidate is eligible at and the replays the verdict is
    /// expected to take, so a resolution rule that picks the wrong index fails on the call.</summary>
    public static IEnumerable<TestCaseData> ClaimCases
    {
        get
        {
            const int end = 3;
            static TestCaseData Case(string name, OmissionClaim[]? claims, int[] eligibleAt, bool satisfied, int[] replayedAt) =>
                new(claims, eligibleAt, satisfied, replayedAt) { TestName = name };
            Hash256 hash = _candidate.Hash!;

            yield return Case("Unclaimed candidate eligible at the end is censoring", null, [end], false, [end]);
            yield return Case("Unclaimed candidate ineligible at the end is excused by replay", null, [], true, [end]);
            yield return Case("Claim excuses a candidate ineligible where the builder tried",
                [new(hash, 1)], [end], true, [1]);
            yield return Case("Claim at index zero is before every block transaction",
                [new(hash, 0)], [end], true, [0]);
            yield return Case("Claim leaves enforced a candidate eligible where the builder tried",
                [new(hash, 1)], [1, end], false, [1]);
            yield return Case("Claim condemns a candidate eligible where claimed though ineligible at the end",
                [new(hash, 1)], [1], false, [1]);
            yield return Case("Out-of-range claim resolves to the end of the payload",
                [new(hash, 99)], [end], false, [end]);
            yield return Case("Claim at the end is the default index", [new(hash, end)], [end], false, [end]);
            yield return Case("Duplicate claims for one hash are all ignored",
                [new(hash, 1), new(hash, 2)], [end], false, [end]);
            yield return Case("A hash claimed three times stays ignored",
                [new(hash, 1), new(hash, 1), new(hash, 2)], [end], false, [end]);
            yield return Case("Each hash resolves its own claim among several",
                [new(Keccak.Compute("elsewhere"), 2), new(hash, 1)], [end], true, [1]);
            yield return Case("Claim for a hash outside the list is ignored",
                [new(Keccak.Compute("elsewhere"), 1)], [end], false, [end]);
        }
    }

    [TestCaseSource(nameof(ClaimCases))]
    public void Profile_2_omission_is_judged_at_the_resolved_evaluation_index(
        OmissionClaim[]? claims, int[] eligibleAt, bool satisfied, int[] replayedAt)
    {
        Profile2Case block = Profile2Block([_candidate], blockTxCount: 3, claims: claims);
        FakeReplayer replayer = new((_, index) => System.Array.IndexOf(eligibleAt, index) >= 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsSatisfied(block, replayer), Is.EqualTo(satisfied));
            Assert.That(replayer.ReplayedAt, Is.EqualTo(replayedAt));
            Assert.That(replayer.Batches, Is.EqualTo(1));
        }
    }

    /// <summary>A claim is looked up only for an omitted candidate, so it cannot touch anything else.</summary>
    [TestCase(true, TestName = "Claim for an included candidate is ignored")]
    [TestCase(false, TestName = "Claim for a non-candidate is ignored")]
    public void Claim_is_ignored_outside_an_omitted_candidate(bool included)
    {
        Transaction entry = included ? _candidate : WithHash(BuildFrameTx([Body(50_000)]), "non-candidate");
        Profile2Case block = Profile2Block([entry], blockTxCount: 3, claims: [new(entry.Hash!, 1)], included: included ? [entry] : []);
        FakeReplayer replayer = new((_, _) => true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsSatisfied(block, replayer), Is.True);
            Assert.That(replayer.ReplayedAt, Is.Empty);
        }
    }

    /// <summary>EIP-7805 § VERIFY budget: the budget filled per committee position, in ascending hash order.
    /// Only the named candidate is eligible, so whether its omission is excused is the fill's verdict alone. A
    /// candidate's order argument is the leading byte of its hash, which fixes where the fill takes it.</summary>
    public static IEnumerable<TestCaseData> BudgetCases
    {
        get
        {
            static TestCaseData Case(string name, Transaction[][] lists, Transaction candidate, bool satisfied, Transaction? badSignature = null) =>
                new(lists, candidate, badSignature, satisfied) { TestName = name };

            // 1-of-N: pooled into one budget, the stuffed position's 1,040,000 would leave the honest one 8,576.
            Transaction honest = Candidate(5, 100_000);
            yield return Case("One position stuffed with VERIFY cost cannot excuse another position's candidate",
                [[Candidate(1, 260_000), Candidate(2, 260_000), Candidate(3, 260_000), Candidate(4, 260_000)], [honest]], honest, false);

            // Of 1,048,576: the four fillers debit 800,000 and leave 248,576, which the second's cost exceeds.
            Transaction[] filler = [Candidate(1, 200_000), Candidate(2, 200_000), Candidate(3, 200_000), Candidate(4, 200_000)];
            Transaction first = filler[0];
            Transaction second = Candidate(5, 250_000);
            yield return Case("Candidate over its own position's remaining budget is excused", [[.. filler, second]], second, true);
            yield return Case("A duplicate in one list is debited once", [[.. filler[..3], first, second]], second, false);
            yield return Case("The fill takes a position's entries by hash, not by arrival", [[second, .. filler]], second, true);
            yield return Case("Candidate carried at several positions is enforced when any admits it",
                [[.. filler, second], [second]], second, false);
            Transaction[] otherFiller = [Candidate(1, 210_000), Candidate(2, 210_000), Candidate(3, 210_000), Candidate(4, 210_000)];
            yield return Case("Candidate carried at several positions is excused when none admits it",
                [[.. filler, second], [.. otherFiller, second]], second, true);

            // The fillers leave 248,576, which the overflow's 250,000 exceeds; ignored without a debit, it leaves
            // room for the last.
            Transaction last = Candidate(6, 200_000);
            yield return Case("Occurrence over the remaining budget is ignored without a debit",
                [[.. filler, Candidate(5, 250_000), last]], last, false);

            // Three fillers leave 448,576; the signed candidate's whole 252,800 then leaves too little for the
            // last, its 2,800 signature cost alone does not.
            Transaction[] signedFiller = filler[..3];
            Transaction signed = Candidate(4, 250_000, signatures: 1);
            Transaction afterSigned = Candidate(5, 250_000);
            // EIP-7805 step 1: entries failing the noncryptographic candidate conditions are skipped without a debit,
            // so four of them at 260,000 leave the whole budget to the honest candidate. A wrong chain ID is a replay
            // check, not one of those conditions, so it debits as a candidate does.
            yield return Case("Entries outside Profile 2 do not debit the budget",
                [[.. Enumerable.Range(1, 4).Select(i => Ordered(BuildFrameTx([Verify(260_000)]), (byte)i)), honest]], honest, false);
            yield return Case("Statically invalid entries do not debit the budget",
                [[.. Enumerable.Range(1, 4).Select(i => Ordered(BuildFrameTx([SelfVerify(260_000, UInt256.One)]), (byte)i)), honest]], honest, false);
            yield return Case("Entries with another chain ID debit the budget",
                [[.. Enumerable.Range(1, 4).Select(i => Ordered(WithChainId(Candidate((byte)i, 260_000), 999), (byte)i)), honest]], honest, true);
            yield return Case("Invalid signatures keep only the signature debit", [[.. signedFiller, signed, afterSigned]], afterSigned, false, badSignature: signed);
            yield return Case("Valid signatures debit the whole cost", [[.. signedFiller, signed, afterSigned]], afterSigned, true);
        }
    }

    [TestCaseSource(nameof(BudgetCases))]
    public void Profile_2_verify_budget_is_filled_per_committee_position(
        Transaction[][] lists, Transaction candidate, Transaction? badSignature, bool satisfied)
    {
        Profile2Case block = Profile2Block(lists, blockTxCount: 0);
        FakeReplayer replayer = new((tx, _) => ReferenceEquals(tx, candidate), tx => !ReferenceEquals(tx, badSignature));

        Assert.That(IsSatisfied(block, replayer), Is.EqualTo(satisfied));
    }

    /// <summary>EIP-7805 § Separate inclusion lists: a validator at several positions has the same list at each, and
    /// each identical fill has its own budget without multiplying anyone's.</summary>
    [Test]
    public void Identical_lists_at_several_positions_fill_independently()
    {
        Transaction[] filler = [Candidate(1, 200_000), Candidate(2, 200_000), Candidate(3, 200_000), Candidate(4, 200_000)];
        Transaction over = Candidate(5, 250_000);
        Transaction[] list = [.. filler, over];

        using (Assert.EnterMultipleScope())
        {
            // Over its list's remaining budget at every position, so repeating the list admits it nowhere.
            Assert.That(IsSatisfied(Profile2Block([list, list, list], blockTxCount: 0), new FakeReplayer((tx, _) => ReferenceEquals(tx, over))), Is.True);
            // Within budget at one position of its own, so the stuffed copies elsewhere cannot excuse it.
            Assert.That(IsSatisfied(Profile2Block([list, list, [over]], blockTxCount: 0), new FakeReplayer((tx, _) => ReferenceEquals(tx, over))), Is.False);
        }
    }

    /// <summary>An entry carried at every committee position is verified once, not once per position.</summary>
    [Test]
    public void Signatures_are_verified_once_per_entry_across_positions()
    {
        Transaction signed = Candidate(1, 100_000, signatures: 1);
        Transaction[][] lists = [.. Enumerable.Repeat(new[] { signed, Candidate(2, 100_000) }, Eip7805Constants.InclusionListCommitteeSize)];
        FakeReplayer replayer = new((_, _) => false);

        IsSatisfied(Profile2Block(lists, blockTxCount: 0), replayer);

        // Both entries sit at all 16 positions: 2 verifications, not 32.
        Assert.That(replayer.SignatureChecks, Is.EqualTo(2));
    }

    /// <summary>Every omitted candidate of a block, at every index it is judged at, reaches the replayer in one
    /// batch, so the block's state is reconstructed once however many candidates and claimed indices there are.</summary>
    [Test]
    public void Candidates_are_replayed_in_one_batch()
    {
        Transaction[] candidates = [Candidate(1, 100_000), Candidate(2, 100_000), Candidate(3, 100_000)];
        Profile2Case block = Profile2Block(candidates, blockTxCount: 3,
            claims: [new(candidates[0].Hash!, 0), new(candidates[1].Hash!, 2)]);
        FakeReplayer replayer = new((_, _) => false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsSatisfied(block, replayer), Is.True);
            Assert.That(replayer.Batches, Is.EqualTo(1));
            Assert.That(replayer.ReplayedAt, Is.EquivalentTo(new[] { 0, 2, 3 }));
        }
    }

    /// <summary>EIP-7805: a claimed candidate must fit the gas the block used before its claim, not after the
    /// whole payload. The block is full at the end, so only the gas before the claim lets it fit.</summary>
    [TestCase(true, false, TestName = "Claim before a full block's transactions is judged on the gas used before it")]
    [TestCase(false, true, TestName = "Without per-transaction gas a claim is judged on the whole block's gas")]
    public void Claimed_candidate_fits_the_gas_used_before_its_index(bool withGasUsedBefore, bool satisfied)
    {
        Profile2Case profile2 = Profile2Block([_candidate], blockTxCount: 3, claims: [new(_candidate.Hash!, 1)]);
        Block block = profile2.Block;
        block.Header.GasUsed = block.GasLimit;
        block.Header.GasUsedPerDimension = (block.GasLimit, block.GasLimit);
        (ulong, ulong)[] gasUsedBefore = [(0, 0), (21_000, 0), (block.GasLimit - 21_000, 0)];
        FakeReplayer replayer = new((_, index) => index == 1);

        bool result = IsSatisfied(profile2, replayer, withGasUsedBefore ? gasUsedBefore : null);

        Assert.That(result, Is.EqualTo(satisfied));
    }

    /// <summary>The gas used before each transaction is read off the receipts only when they add up to the block's totals.</summary>
    [TestCase(30_000UL, 5_000UL, true, TestName = "Receipts adding up to the block's gas give the gas before each transaction")]
    [TestCase(30_001UL, 5_000UL, false, TestName = "Receipts not adding up to the block's gas give nothing")]
    public void Gas_used_before_each_transaction_comes_from_consistent_receipts(ulong totalExecution, ulong totalState, bool consistent)
    {
        Block block = Profile2Block(Array.Empty<Transaction>(), blockTxCount: 2).Block;
        block.Header.GasUsedPerDimension = (totalExecution, totalState);
        TxReceipt[] receipts = [new() { BlockGasUsed = 21_000, StorageGasUsed = 5_000 }, new() { BlockGasUsed = 9_000 }];

        (ulong Execution, ulong State)[]? before = InclusionListValidator.GasUsedBeforeEachTransaction(block, receipts);

        Assert.That(before, consistent ? Is.EqualTo(new (ulong, ulong)[] { (0, 0), (21_000, 5_000) }) : Is.Null);
    }

    /// <summary>Pads <paramref name="lists"/> with empty lists to one per committee position.</summary>
    private static Transaction[][] AtPositions(params Transaction[][] lists)
    {
        Transaction[][] positions = new Transaction[Eip7805Constants.InclusionListCommitteeSize][];
        for (int position = 0; position < positions.Length; position++) positions[position] = position < lists.Length ? lists[position] : [];
        return positions;
    }

    private sealed class FakeReplayer(Func<Transaction, int, bool> eligible, Func<Transaction, bool>? signaturesValid = null)
        : IProfile2EligibilityReplayer
    {
        public List<int> ReplayedAt { get; } = [];

        public int Batches { get; private set; }

        public int SignatureChecks { get; private set; }

        public bool AreSignaturesValid(Transaction transaction, IReleaseSpec spec)
        {
            SignatureChecks++;
            return signaturesValid?.Invoke(transaction) ?? true;
        }

        public bool[] AreEligible(Block block, IReadOnlyList<(Transaction Transaction, int Index)> requests, IReleaseSpec spec)
        {
            Batches++;
            bool[] verdicts = new bool[requests.Count];
            for (int i = 0; i < requests.Count; i++)
            {
                ReplayedAt.Add(requests[i].Index);
                verdicts[i] = eligible(requests[i].Transaction, requests[i].Index);
            }
            return verdicts;
        }
    }

    /// <summary>A block with the separate lists and claims it is checked against.</summary>
    private sealed record Profile2Case(Block Block, Transaction[][] Lists, OmissionClaim[]? Claims);

    private static bool IsSatisfied(Profile2Case profile2, IProfile2EligibilityReplayer replayer, (ulong, ulong)[]? gasUsedBefore = null) =>
        InclusionListValidator.IsSatisfied(profile2.Block, profile2.Lists, StateWith(TestItem.AddressA, 10.Ether, 0), _frameSpecProvider.GetSpec(profile2.Block.Header),
            _txValidator, Eip8369Constants.MaxVerifyGasPerTx, profile2.Claims, replayer, gasUsedBefore);

    /// <summary>Whether <paramref name="block"/> honours <paramref name="il"/>, the list at committee position 0.</summary>
    private static bool IsSatisfied(Block block, Transaction[] il, IReadOnlyStateProvider state, IReleaseSpec spec) =>
        InclusionListValidator.IsSatisfied(block, AtPositions(il), state, spec, _txValidator, Eip8369Constants.MaxVerifyGasPerTx, claims: null);

    /// <summary>A block omitting <paramref name="il"/>, which sits at committee position 0.</summary>
    private static Profile2Case Profile2Block(Transaction[] il, int blockTxCount, OmissionClaim[]? claims = null, Transaction[]? included = null) =>
        Profile2Block([il], blockTxCount, claims, included);

    /// <summary>A block omitting <paramref name="lists"/>, one per committee position from position 0.</summary>
    private static Profile2Case Profile2Block(Transaction[][] lists, int blockTxCount, OmissionClaim[]? claims = null, Transaction[]? included = null)
    {
        List<Transaction> blockTxs = [.. included ?? []];
        for (int i = blockTxs.Count; i < blockTxCount; i++)
            blockTxs.Add(BuildTx(nonce: (ulong)i + 10, to: TestItem.AddressB));

        Transaction[][] positions = AtPositions(lists);
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions([.. blockTxs])
            .TestObject;
        return new Profile2Case(block, positions, claims);
    }

    /// <param name="order">The hash's leading byte, which places the candidate in a position's fill order.</param>
    private static Transaction Candidate(byte order, ulong verifyGas, int signatures = 0)
    {
        Transaction tx = WithHash(BuildFrameTx([SelfVerify(verifyGas)]), $"candidate-{order}-{verifyGas}-{signatures}");
        byte[] hash = tx.Hash!.BytesToArray();
        hash[0] = order;
        tx.Hash = new Hash256(hash);
        tx.FrameSignatures = new TxFrameSignature[signatures];
        for (int i = 0; i < signatures; i++)
            tx.FrameSignatures[i] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, ReadOnlyMemory<byte>.Empty, new byte[65]);
        return tx;
    }

    /// <summary>Gives <paramref name="tx"/> a hash whose leading byte is <paramref name="order"/>.</summary>
    private static Transaction Ordered(Transaction tx, byte order)
    {
        byte[] hash = Keccak.Compute($"ordered-{order}-{tx.Frames![0].ExecutionGasLimit}-{tx.Frames[0].Mode}-{tx.Frames[0].Value}").BytesToArray();
        hash[0] = order;
        tx.Hash = new Hash256(hash);
        return tx;
    }

    private static Transaction WithChainId(Transaction tx, ulong chainId)
    {
        tx.ChainId = chainId;
        return tx;
    }

    private static Transaction WithHash(Transaction tx, string id)
    {
        tx.Hash = Keccak.Compute(id);
        return tx;
    }

    // A self-relay prefix: the sender approves its own execution and payment, so it pays for itself.
    private static TxFrame SelfVerify(ulong gasLimit = 100_000, UInt256 value = default) =>
        new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit, value, default);

    private static TxFrame Body(ulong gasLimit) =>
        new(FrameMode.Default, FrameFlags.None, TestItem.AddressC, gasLimit, UInt256.Zero, default);

    private static TxFrame Verify(ulong gasLimit) =>
        new(FrameMode.Verify, FrameFlags.None, TestItem.AddressC, gasLimit, UInt256.Zero, default);

    // An `only_verify | pay` prefix nominating AddressB as the payer.
    private static TxFrame[] SponsoredFrames =>
    [
        new(FrameMode.Verify, FrameFlags.ApproveExecution, target: null, 100_000, UInt256.Zero, default),
        new(FrameMode.Verify, FrameFlags.ApprovePayment, TestItem.AddressB, 100_000, UInt256.Zero, default),
    ];

    private static Transaction BuildFrameTx(TxFrame[]? frames = null, int blobCount = 0, UInt256? maxFeePerGas = null)
    {
        frames ??= [SelfVerify()];
        return new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            SenderAddress = TestItem.AddressA,
            Nonce = 0,
            // EIP-8250 replaces the scalar nonce; the zero key alone is the account nonce.
            NonceKeys = [UInt256.Zero],
            Frames = frames,
            FrameSignatures = [],
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = maxFeePerGas ?? 10.GWei,
            MaxFeePerBlobGas = blobCount == 0 ? null : 1.GWei,
            BlobVersionedHashes = blobCount == 0 ? null : Build.A.Transaction.WithBlobVersionedHashes(blobCount).TestObject.BlobVersionedHashes,
        };
    }

    [Test]
    public void When_il_disabled_by_spec_then_accept_even_if_excluded()
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .TestObject;

        Assert.That(IsSatisfied(block, [_validTx], StateWith(TestItem.AddressA, 10.Ether, 0), Prague.Instance), Is.True);
    }

    // EIP-3607: a sender that has deployed (non-delegation) code cannot send a tx.
    [TestCase(false, ExpectedResult = true, TestName = "Sender with non-delegated code is not appendable")]
    // EIP-7702 delegation: a sender with delegation code IS allowed to send txs.
    [TestCase(true, ExpectedResult = false, TestName = "Sender with delegated code is appendable")]
    public bool Sender_with_code_appendability_depends_on_delegation(bool isDelegated)
    {
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        ValueHash256 codeHash = new("0x" + new string('a', 64));
        state.TryGetAccount(TestItem.AddressA, out Arg.Any<AccountStruct>()).Returns(call =>
        {
            // Any non-empty codehash → HasCode = true.
            call[1] = new AccountStruct(0UL, 10.Ether, Keccak.EmptyTreeHash, codeHash);
            return true;
        });
        state.IsDelegatedCode(codeHash).Returns(isDelegated);

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .TestObject;

        return IsSatisfied(block, [_validTx], state, _specProvider.GetSpec(block.Header));
    }

    private static Transaction BuildTx(ulong gasLimit = 100_000, ulong nonce = 0, UInt256? gasPrice = null, UInt256? value = null, Address? to = null) =>
        Build.A.Transaction
            .WithGasLimit(gasLimit)
            .WithGasPrice(gasPrice ?? 10.GWei)
            .WithNonce(nonce)
            .WithValue(value ?? 1.Ether)
            .WithTo(to ?? TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

    private static Transaction Build1559Tx() =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithGasLimit(100_000)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerGas(10.GWei)
            .WithNonce(0)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

    // Rejected by normal transaction validation, so an omitted entry like this is not appendable.
    private static Transaction BuildMalformed1559Tx() =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithGasLimit(100_000)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithMaxFeePerGas(1.GWei)
            .WithNonce(0)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

    private static Transaction BuildBlobTx(UInt256? maxFeePerBlobGas = null) =>
        Build.A.Transaction
            .WithType(TxType.Blob)
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(maxFeePerBlobGas ?? 10.GWei)
            .WithBlobVersionedHashes(1)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithNonce(0)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

    private static IReadOnlyStateProvider StateWith(params (Address Address, UInt256 Balance, ulong Nonce)[] accounts)
    {
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        foreach ((Address address, UInt256 balance, ulong nonce) in accounts)
        {
            state.TryGetAccount(address, out Arg.Any<AccountStruct>()).Returns(call =>
            {
                call[1] = new AccountStruct(nonce, balance);
                return true;
            });
        }

        return state;
    }

    private static IReadOnlyStateProvider StateWith(Address sender, UInt256 balance, ulong nonce)
    {
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.TryGetAccount(sender, out Arg.Any<AccountStruct>()).Returns(call =>
        {
            call[1] = new AccountStruct(nonce, balance);
            return true;
        });
        return state;
    }
}
