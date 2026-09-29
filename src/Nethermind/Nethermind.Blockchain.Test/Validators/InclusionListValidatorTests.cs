// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    // Bogota carries inclusion lists alone; a chain wanting frame transactions too schedules their
    // transition alongside it. That combination is what makes a frame transaction reachable as an entry.
    private static readonly ISpecProvider _frameSpecProvider = new CustomSpecProvider(
        ((ForkActivation)0, new OverridableReleaseSpec(Bogota.Instance) { IsEip8141Enabled = true }));
    private static readonly TxValidator _txValidator = new(TestBlockchainIds.ChainId);
    private static readonly Transaction _validTx = BuildTx();

    public static IEnumerable<TestCaseData> SatisfactionCases
    {
        get
        {
            static TestCaseData Case(string name, Transaction[]? il, bool satisfied, Transaction[]? blockTxs = null, ulong gasUsed = 1_000_000, UInt256 baseFee = default, ulong senderNonce = 0) =>
                new(blockTxs ?? [], il, gasUsed, baseFee, senderNonce, satisfied) { TestName = name };

            yield return Case("Full block is satisfied", [_validTx], true, gasUsed: 30_000_000);
            yield return Case("All IL txs included", [_validTx], true, blockTxs: [_validTx]);
            yield return Case("Appendable IL tx excluded", [_validTx], false);
            // Null IL = non-engine-API path (genesis, RLP import); validator treats as not applicable.
            yield return Case("No IL", null, true);
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
            // The blob carve-out applies to building an IL, not to judging one.
            yield return Case("Blob tx", [BuildBlobTx()], false);
            // Blob gas is paid up front, so a blob fee beyond the balance makes the tx unappendable.
            yield return Case("Blob tx cannot afford blob fee", [BuildBlobTx(maxFeePerBlobGas: 100_000.GWei)], true);
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

    [TestCaseSource(nameof(SatisfactionCases))]
    public void Inclusion_list_satisfaction(Transaction[] blockTxs, Transaction[]? il, ulong gasUsed, UInt256 baseFee, ulong senderNonce, bool satisfied)
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(gasUsed)
            .WithBaseFeePerGas(baseFee)
            .WithTransactions(blockTxs)
            .WithInclusionListTransactions(il)
            .TestObject;

        IReadOnlyStateProvider state = StateWith(TestItem.AddressA, 10.Ether, senderNonce);
        Assert.That(InclusionListValidator.IsSatisfied(block, state, _specProvider.GetSpec(block.Header), _txValidator), Is.EqualTo(satisfied));
    }

    // Withdrawals land after the block's transactions, so judging against the raw post-block balance
    // would make an honest builder look like a censor.
    [TestCase(0UL, ExpectedResult = false, TestName = "Sender funded before withdrawals is appendable")]
    [TestCase(9_500_000_000UL, ExpectedResult = true, TestName = "Sender funded only by this block's withdrawal is not appendable")]
    public bool Withdrawals_are_not_spendable_by_an_appended_tx(ulong withdrawnGwei)
    {
        Withdrawal[] withdrawals = withdrawnGwei == 0
            ? []
            : [Build.A.Withdrawal.WithRecipient(TestItem.AddressA).WithAmount(withdrawnGwei).TestObject];

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithBaseFeePerGas(UInt256.Zero)
            .WithTransactions([])
            .WithWithdrawals(withdrawals)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        // Withdrawing 9.5 of the 10 ether leaves 0.5, below _validTx's ~1.001 ether cost.
        return InclusionListValidator.IsSatisfied(block, StateWith(TestItem.AddressA, 10.Ether, 0), _specProvider.GetSpec(block.Header), _txValidator);
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
            yield return Case("Omitted frame transaction over the VERIFY budget is judged once the cap is lifted",
                [BuildFrameTx([SelfVerify(2_000_000)])], false, maxVerifyGasPerTx: 0);
            // Condition 1: a candidate shape is not enough, the transaction must also be statically valid.
            yield return Case("Omitted malformed frame transaction is excused",
                [BuildFrameTx([SelfVerify(value: UInt256.One)])], true, wellFormed: false);

            // A sponsored transaction is judged against the payer its prefix nominates, not against its sender.
            yield return Case("Sponsored frame transaction is judged against its payer, not its sender",
                [BuildFrameTx(SponsoredFrames)], false, payerBalance: 10.Ether, senderBalance: UInt256.One);
            yield return Case("Sponsored frame transaction whose payer cannot pay is excused",
                [BuildFrameTx(SponsoredFrames)], true, payerBalance: UInt256.One);

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
            .WithInclusionListTransactions(il)
            .TestObject;

        return InclusionListValidator.IsSatisfied(
            block, StateWith(TestItem.AddressA, 10.Ether, 0), _frameSpecProvider.GetSpec(block.Header), _txValidator);
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
            .WithInclusionListTransactions(il)
            .TestObject;
        IReleaseSpec spec = _frameSpecProvider.GetSpec(block.Header);
        IReadOnlyStateProvider state = StateWith((TestItem.AddressA, senderBalance, 0), (TestItem.AddressB, payerBalance, 0));

        using (Assert.EnterMultipleScope())
        {
            // Keeps every case honest: an entry could otherwise be excused for being malformed instead.
            foreach (Transaction tx in il)
                if (tx.SupportsFrames)
                    Assert.That((bool)_txValidator.IsWellFormed(tx, spec, block.GasLimit), Is.EqualTo(wellFormed));

            Assert.That(InclusionListValidator.IsSatisfied(block, state, spec, _txValidator, maxVerifyGasPerTx), Is.EqualTo(satisfied));
        }
    }

    private static readonly Transaction _candidate = Candidate(1, 100_000);

    /// <summary>EIP-8369 § Builders and § Attesters: the index an omitted Profile 2 candidate is replayed at,
    /// and how a claim moves it. Each case names the indices the candidate is eligible at and the replays
    /// the verdict is expected to take, so a resolution rule that picks the wrong index fails on the call.</summary>
    public static IEnumerable<TestCaseData> ClaimCases
    {
        get
        {
            const int end = 3;
            static TestCaseData Case(string name, InclusionListClaim[]? claims, int[] eligibleAt, bool satisfied, int[] replayedAt) =>
                new(claims, eligibleAt, satisfied, replayedAt) { TestName = name };
            Hash256 hash = _candidate.Hash!;

            yield return Case("Unclaimed candidate eligible at the end is censoring", null, [end], false, [end]);
            yield return Case("Unclaimed candidate ineligible at the end is excused by replay", null, [], true, [end]);
            yield return Case("Claim excuses a candidate ineligible where the builder tried",
                [new(hash, 1)], [end], true, [end, 1]);
            yield return Case("Claim at index zero is before every block transaction",
                [new(hash, 0)], [end], true, [end, 0]);
            yield return Case("Claim leaves enforced a candidate eligible where the builder tried",
                [new(hash, 1)], [1, end], false, [end, 1]);
            yield return Case("Claim cannot condemn a candidate eligible only before the end",
                [new(hash, 1)], [1], true, [end]);
            yield return Case("Out-of-range claim resolves to the end of the payload",
                [new(hash, 99)], [end], false, [end]);
            yield return Case("Claim at the end is the default index", [new(hash, end)], [end], false, [end]);
            yield return Case("Duplicate claims for one hash are all ignored",
                [new(hash, 1), new(hash, 2)], [end], false, [end]);
            yield return Case("Claim for a hash outside the list is ignored",
                [new(Keccak.Compute("elsewhere"), 1)], [end], false, [end]);
        }
    }

    [TestCaseSource(nameof(ClaimCases))]
    public void Profile_2_omission_is_judged_at_the_resolved_evaluation_index(
        InclusionListClaim[]? claims, int[] eligibleAt, bool satisfied, int[] replayedAt)
    {
        Block block = Profile2Block([_candidate], blockTxCount: 3, claims: claims);
        FakeReplayer replayer = new((_, index) => System.Array.IndexOf(eligibleAt, index) >= 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsSatisfied(block, replayer), Is.EqualTo(satisfied));
            Assert.That(replayer.ReplayedAt, Is.EqualTo(replayedAt));
        }
    }

    /// <summary>A claim is looked up only for an omitted candidate, so it cannot touch anything else.</summary>
    [TestCase(true, TestName = "Claim for an included candidate is ignored")]
    [TestCase(false, TestName = "Claim for a non-candidate is ignored")]
    public void Claim_is_ignored_outside_an_omitted_candidate(bool included)
    {
        Transaction entry = included ? _candidate : WithHash(BuildFrameTx([Body(50_000)]), "non-candidate");
        Block block = Profile2Block([entry], blockTxCount: 3, claims: [new(entry.Hash!, 1)], included: included ? [entry] : []);
        FakeReplayer replayer = new((_, _) => true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsSatisfied(block, replayer), Is.True);
            Assert.That(replayer.ReplayedAt, Is.Empty);
        }
    }

    /// <summary>EIP-8369 § Includers: the VERIFY budget filled per committee position, in ascending hash order.
    /// Only the named candidate is eligible, so whether its omission is excused is the fill's verdict alone. A
    /// candidate's order argument is the leading byte of its hash, which fixes where the fill takes it.</summary>
    public static IEnumerable<TestCaseData> BudgetCases
    {
        get
        {
            static TestCaseData Case(string name, Transaction[][] lists, Transaction candidate, bool satisfied,
                bool withMembership = true, Transaction? badSignature = null) =>
                new(lists, withMembership, candidate, badSignature, satisfied) { TestName = name };

            // 1-of-N: pooled into one budget, the stuffed position's 1,040,000 would leave the honest one 8,576.
            Transaction honest = Candidate(3, 100_000);
            yield return Case("One position stuffed with VERIFY cost cannot excuse another position's candidate",
                [[Candidate(1, 520_000), Candidate(2, 520_000)], [honest]], honest, false);

            Transaction first = Candidate(1, 600_000);
            Transaction second = Candidate(3, 600_000);
            yield return Case("Candidate over its own position's remaining budget is excused", [[first, second]], second, true);
            yield return Case("A list without membership applies no per-position budget", [[first, second]], second, false, withMembership: false);
            yield return Case("The fill takes a position's entries by hash, not by arrival", [[second, first]], second, true);
            yield return Case("Candidate carried at several positions is enforced when any admits it",
                [[first, second], [second]], second, false);
            yield return Case("Candidate carried at several positions is excused when none admits it",
                [[first, second], [Candidate(2, 600_000), second]], second, true);

            // Of 1,048,576: the first debits 700,000 and leaves 348,576, which the second's cost exceeds.
            Transaction last = Candidate(3, 300_000);
            yield return Case("Occurrence over the remaining budget is ignored without a debit",
                [[Candidate(1, 700_000), Candidate(2, 700_000), last]], last, false);

            Transaction signed = Candidate(1, 700_000, signatures: 1);
            Transaction afterSigned = Candidate(2, 400_000);
            yield return Case("Invalid signatures keep only the signature debit", [[signed, afterSigned]], afterSigned, false, badSignature: signed);
            yield return Case("Valid signatures debit the whole cost", [[signed, afterSigned]], afterSigned, true);
        }
    }

    [TestCaseSource(nameof(BudgetCases))]
    public void Profile_2_verify_budget_is_filled_per_committee_position(
        Transaction[][] lists, bool withMembership, Transaction candidate, Transaction? badSignature, bool satisfied)
    {
        (Transaction[] il, ushort[] membership) = Membership(lists);
        Block block = Profile2Block(il, blockTxCount: 0, membership: withMembership ? membership : null);
        FakeReplayer replayer = new((tx, _) => ReferenceEquals(tx, candidate), tx => !ReferenceEquals(tx, badSignature));

        Assert.That(IsSatisfied(block, replayer), Is.EqualTo(satisfied));
    }

    /// <summary>A membership that is not one mask per entry attributes nothing, so no per-position budget applies.</summary>
    [TestCase(false, true, TestName = "Aligned membership fills the per-position budget")]
    [TestCase(true, false, TestName = "Misaligned membership is ignored")]
    public void Misaligned_membership_is_ignored(bool truncate, bool satisfied)
    {
        Transaction first = Candidate(1, 600_000);
        Transaction second = Candidate(2, 600_000);
        (Transaction[] il, ushort[] membership) = Membership([[first, second]]);
        Block block = Profile2Block(il, blockTxCount: 0, membership: truncate ? membership[..1] : membership);

        Assert.That(IsSatisfied(block, new FakeReplayer((tx, _) => ReferenceEquals(tx, second))), Is.EqualTo(satisfied));
    }

    /// <summary>Deduplicates committee positions' lists into one list and a membership mask per entry.</summary>
    private static (Transaction[] Il, ushort[] Membership) Membership(Transaction[][] lists)
    {
        List<Transaction> il = [];
        List<ushort> masks = [];
        for (int position = 0; position < lists.Length; position++)
        {
            foreach (Transaction tx in lists[position])
            {
                int index = il.IndexOf(tx);
                if (index < 0)
                {
                    il.Add(tx);
                    masks.Add(0);
                    index = il.Count - 1;
                }
                masks[index] |= (ushort)(1 << position);
            }
        }
        return ([.. il], [.. masks]);
    }

    private sealed class FakeReplayer(Func<Transaction, int, bool> eligible, Func<Transaction, bool>? signaturesValid = null)
        : IProfile2EligibilityReplayer
    {
        public List<int> ReplayedAt { get; } = [];

        public bool AreSignaturesValid(Transaction transaction, IReleaseSpec spec) => signaturesValid?.Invoke(transaction) ?? true;

        public bool IsEligible(Block block, Transaction transaction, int index, IReleaseSpec spec)
        {
            ReplayedAt.Add(index);
            return eligible(transaction, index);
        }
    }

    private static bool IsSatisfied(Block block, IProfile2EligibilityReplayer replayer) =>
        InclusionListValidator.IsSatisfied(block, StateWith(TestItem.AddressA, 10.Ether, 0), _frameSpecProvider.GetSpec(block.Header), _txValidator, replayer: replayer);

    private static Block Profile2Block(Transaction[] il, int blockTxCount, InclusionListClaim[]? claims = null, ushort[]? membership = null, Transaction[]? included = null)
    {
        List<Transaction> blockTxs = [.. included ?? []];
        for (int i = blockTxs.Count; i < blockTxCount; i++)
            blockTxs.Add(BuildTx(nonce: (ulong)i + 10, to: TestItem.AddressB));

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions([.. blockTxs])
            .WithInclusionListTransactions(il)
            .TestObject;
        block.InclusionListMembership = membership;
        block.InclusionListClaims = claims;
        return block;
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
            tx.FrameSignatures[i] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, new byte[32], new byte[65]);
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
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        Assert.That(InclusionListValidator.IsSatisfied(block, StateWith(TestItem.AddressA, 10.Ether, 0), Prague.Instance, _txValidator), Is.True);
    }

    // EIP-3607: a sender that has deployed (non-delegation) code cannot send a tx.
    [TestCase(false, ExpectedResult = true, TestName = "Sender with non-delegated code is not appendable")]
    // EIP-7702 delegation: a sender with delegation code IS allowed to send txs.
    [TestCase(true, ExpectedResult = false, TestName = "Sender with delegated code is appendable")]
    public bool Sender_with_code_appendability_depends_on_delegation(bool isDelegated)
    {
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.TryGetAccount(TestItem.AddressA, out Arg.Any<AccountStruct>()).Returns(call =>
        {
            // Any non-empty codehash → HasCode = true.
            call[1] = new AccountStruct(0UL, 10.Ether, Keccak.EmptyTreeHash, new ValueHash256("0x" + new string('a', 64)));
            return true;
        });
        state.IsDelegatedCode(TestItem.AddressA).Returns(isDelegated);

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        return InclusionListValidator.IsSatisfied(block, state, _specProvider.GetSpec(block.Header), _txValidator);
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
