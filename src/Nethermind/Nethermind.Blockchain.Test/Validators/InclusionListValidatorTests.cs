// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
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
    private const bool Proven = true;

    /// <summary>A counted list as newPayload resolves it: with a verified proof covering its entries, or with none.</summary>
    private static CommitteeInclusionList Counted(Transaction[] transactions, bool proven)
        => new(transactions)
        {
            VerifiedProofDeps = proven
                ? transactions.SelectMany(static tx => Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)).ToHashSet()
                : null
        };

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void Lists_without_a_verified_proof_establish_no_mandatory_dependency_transactions(bool dependencies, bool full)
    {
        Transaction transaction = BuildFrameTx();
        byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
        data[31] = Eip8288Constants.LeanSphincsScheme;
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null,
            Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, data)];
        Block block = Build.A.Block.WithGasLimit(30_000_000).WithGasUsed(full ? 30_000_000UL : 0)
            .WithInclusionListTransactions(dependencies ? [transaction] : []).TestObject;
        // A list without a verified proof obligates only dependency-free entries; an empty list binds nothing whatever it proves.
        block.InclusionLists = [Counted(block.InclusionListTransactions!, !dependencies)];
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };

        Assert.That(IsSatisfied(block, StateWith(TestItem.AddressA, 10.Ether, 0), spec, _txValidator), Is.True);
    }

    private static bool IsSatisfied(Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator validator)
        => IsSatisfied(block, block.InclusionListTransactions, state, spec, validator);

    private static bool IsSatisfied(Block block, Transaction[]? list, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator validator)
        => InclusionListValidator.IsSatisfied(block, list, state, spec, validator, block.InclusionLists);

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

    [TestCaseSource(nameof(SatisfactionCases))]
    public void Inclusion_list_satisfaction(Transaction[] blockTxs, Transaction[]? il, ulong gasUsed, UInt256 baseFee, ulong senderNonce, bool satisfied)
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(gasUsed)
            .WithBaseFeePerGas(baseFee)
            .WithTransactions(blockTxs)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithInclusionListTransactions(il)
            .TestObject;

        IReadOnlyStateProvider state = StateWith(TestItem.AddressA, 10.Ether, senderNonce);
        Assert.That(IsSatisfied(block, state, _specProvider.GetSpec(block.Header), _txValidator), Is.EqualTo(satisfied));
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
            .WithInclusionListTransactions([BuildTx(gasLimit: ilGasLimit, to: TestItem.AddressB)])
            .TestObject;
        if (dimensionsKnown) block.Header.GasUsedPerDimension = (513_317, 97_920);

        return IsSatisfied(block, StateWith(TestItem.AddressA, 10.Ether, 0), _specProvider.GetSpec(block.Header), _txValidator);
    }

    // Judging a frame transaction by the Profile 1 rules would read the account nonce it does not use. The
    // well-formedness assertion keeps the case honest: without it the entry could pass for being malformed.
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
        IReleaseSpec spec = _frameSpecProvider.GetSpec(block.Header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool)_txValidator.IsWellFormed(frameTx, spec, block.GasLimit), Is.True);
            Assert.That(IsSatisfied(block, StateWith(TestItem.AddressA, 10.Ether, 0), spec, _txValidator), Is.True);
        }
    }

    // EIP-8288: omission is permitted only when EIP-7805 excuses it. No block dependency count or weighted budget, local proof
    // envelope or wrapper limit is an omission ground, however large the block's dependency union already is.
    [TestCase(Eip8288Constants.MaxProofDependencies, false, TestName = "Full proof envelope is no omission ground")]
    [TestCase(4096, false, TestName = "Large dependency union is no omission ground")]
    [TestCase(4096, true, TestName = "Dependency already in a large block")]
    public void No_dependency_capacity_excuses_an_omission(int blockDependencies, bool present)
    {
        FrameDependency[] blockDeps = new FrameDependency[blockDependencies];
        for (int i = 0; i < blockDependencies; i++)
            blockDeps[i] = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(i.ToString()), default);
        Transaction blockTx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressB,
            Frames = [.. blockDeps.Chunk(Eip8288Constants.MaxDependenciesPerFrame)
                .Select(static chunk => new TxFrame(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, Eip8288Dependencies.Serialize(chunk)))],
            FrameSignatures = [],
        };
        blockTx.Hash = blockTx.CalculateHash();

        FrameDependency dependency = present ? blockDeps[^1] : new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("omitted"), default);
        Transaction entry = BuildFrameTx();
        entry.GasLimit = 1_000_000;
        entry.Frames = [.. entry.Frames!, new TxFrame(FrameMode.DepVerify, FrameFlags.None, null,
            Eip8288Constants.LeanSphincsVerificationGas,
            UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))];
        entry.Hash = entry.CalculateHash();

        Block block = Build.A.Block.WithGasLimit(30_000_000).WithGasUsed(1_000_000).WithTransactions(blockTx)
            .WithInclusionListTransactions([entry]).TestObject;
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };

        CommitteeInclusionList[] lists = [Counted([entry], Proven)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(InclusionListProofValidator.SelectEligible([entry], lists, spec), Has.Length.EqualTo(1));
            Assert.That(InclusionListValidator.IsSatisfied(block, [entry], StateWith(TestItem.AddressA, 10.Ether, 0), spec, _txValidator,
                lists, frameCanInclude: static _ => true), Is.False);
        }
    }

    // EIPs#12475: obligations follow from each signed list alone. An entry with dependencies binds through a list whose proof
    // verifies and covers it, and not through a list without one; an independent proven list can still require it.
    [TestCase(false, false, ExpectedResult = true, TestName = "Entry under only an unproven list imposes no obligation")]
    [TestCase(false, true, ExpectedResult = false, TestName = "Another proven list still requires the entry")]
    [TestCase(true, false, ExpectedResult = false, TestName = "Entry under a proven list is required")]
    public bool Each_list_proof_derives_its_own_obligations(bool firstProven, bool secondList)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("entry"), default);
        Transaction entry = BuildFrameTx();
        entry.GasLimit = 1_000_000;
        entry.Frames = [.. entry.Frames!, new TxFrame(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
            UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))];
        entry.Hash = entry.CalculateHash();
        List<CommitteeInclusionList> lists = [Counted([entry], firstProven)];
        if (secondList) lists.Add(Counted([entry], Proven));
        Block block = Build.A.Block.WithGasLimit(30_000_000).WithGasUsed(1_000_000).TestObject;
        block.InclusionLists = lists;
        block.InclusionListTransactions = CommitteeInclusionList.Flatten(lists);
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };

        return InclusionListValidator.IsSatisfied(block, block.InclusionListTransactions, StateWith(TestItem.AddressA, 10.Ether, 0), spec,
            _txValidator, lists, frameCanInclude: static _ => true);
    }

    private static Transaction BuildFrameTx() => new()
    {
        Type = TxType.FrameTx,
        ChainId = TestBlockchainIds.ChainId,
        SenderAddress = TestItem.AddressA,
        Nonce = 0,
        Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
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

        Assert.That(IsSatisfied(block, StateWith(TestItem.AddressA, 10.Ether, 0), Prague.Instance, _txValidator), Is.True);
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
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        return IsSatisfied(block, state, _specProvider.GetSpec(block.Header), _txValidator);
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
