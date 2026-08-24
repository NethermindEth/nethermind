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

    // EIP-8369: an EIP-8141 frame transaction is a Profile 2 candidate, whose appendability turns on the
    // validation-operand state surface (EIP-8250 keyed nonces, EIP-8272 recent roots, a bounded validation
    // replay) that this validator does not reconstruct. Judging it by the Profile 1 rules would read the
    // account nonce it does not use and report an honest payload as censoring. The well-formedness
    // assertion keeps the case honest: without it the entry could be passing for being malformed.
    [Test]
    public void Omitted_frame_transaction_is_not_judged()
    {
        Transaction frameTx = BuildFrameTx();
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions([])
            .WithInclusionListTransactions([frameTx])
            .TestObject;
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        Assert.That((bool)_txValidator.IsWellFormed(frameTx, spec, block.GasLimit), Is.True);
        Assert.That(InclusionListValidator.IsSatisfied(block, StateWith(TestItem.AddressA, 10.Ether, 0), spec, _txValidator), Is.True);
    }

    private static Transaction BuildFrameTx() => new()
    {
        Type = TxType.FrameTx,
        ChainId = TestBlockchainIds.ChainId,
        SenderAddress = TestItem.AddressA,
        Nonce = 0,
        Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
        FrameSignatures = [],
        GasLimit = 100_000,
        GasPrice = 1.GWei,
        DecodedMaxFeePerGas = 10.GWei,
    };

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
