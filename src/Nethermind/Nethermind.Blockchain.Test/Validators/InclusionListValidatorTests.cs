// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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

            // Outside both EIP-8369 profiles: blob gas has its own budget, over which the EIP defines no check.
            yield return Case("Omitted blob-carrying frame transaction is excused", [BuildFrameTx(blobCount: 1)], true);
            // Condition 2: a prefix matching none of the four admitted shapes.
            yield return Case("Omitted frame transaction with an unrecognized prefix is excused",
                [BuildFrameTx(FramesOf(Body(50_000)))], true);
            // Condition 3: a VERIFY frame behind the validation prefix.
            yield return Case("Omitted frame transaction with a VERIFY frame after its prefix is excused",
                [BuildFrameTx(FramesOf(SelfVerify(), Body(50_000), Verify(50_000)))], true);
            // Condition 4, and the knob that sets it: the same transaction is judged once the cap is lifted.
            yield return Case("Omitted frame transaction over the VERIFY budget is excused",
                [BuildFrameTx(FramesOf(SelfVerify(2_000_000)))], true);
            yield return Case("Omitted frame transaction over the VERIFY budget is judged once the cap is lifted",
                [BuildFrameTx(FramesOf(SelfVerify(2_000_000)))], false, maxVerifyGasPerTx: 0);
            // Condition 1: a candidate shape is not enough, the transaction must also be statically valid.
            yield return Case("Omitted malformed frame transaction is excused",
                [BuildFrameTx(FramesOf(SelfVerify(value: UInt256.One)))], true, wellFormed: false);

            // A sponsored transaction is judged against the payer its prefix nominates, not against its sender.
            yield return Case("Sponsored frame transaction is judged against its payer, not its sender",
                [BuildFrameTx(SponsoredFrames)], false, payerBalance: 10.Ether, senderBalance: UInt256.One);
            yield return Case("Sponsored frame transaction whose payer cannot pay is excused",
                [BuildFrameTx(SponsoredFrames)], true, payerBalance: UInt256.One);

            // The determinate justifications EIP-8369 leaves to the attester's own reading of the payload.
            // 1,105,000 gas remains: the frame limits alone fit, and only the EIP-8141 intrinsic cost on
            // top of them — which Transaction.GasLimit leaves out — puts the reservation over.
            yield return Case("Omitted frame transaction whose max_gas does not fit the remaining gas is excused",
                [BuildFrameTx(FramesOf(SelfVerify(), Body(1_000_000)))], true, gasUsed: 28_895_000);
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
        Transaction[] il = [BuildFrameTx(FramesOf(Body(50_000))), _validTx];
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

    private static TxFrame[] FramesOf(params TxFrame[] frames) => frames;

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
