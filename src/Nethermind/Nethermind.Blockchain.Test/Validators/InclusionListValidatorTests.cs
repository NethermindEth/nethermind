// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class InclusionListValidatorTests
{
    private const ulong TenEther = 10_000_000_000_000_000_000ul;

    private ISpecProvider _specProvider = null!;
    private InclusionListValidator _inclusionListValidator = null!;
    private Transaction _validTx = null!;

    /// <summary>
    /// Parent-state snapshot fixture: AddressA at nonce 0 with 10 ETH. The IL validator no
    /// longer reads from a live worldstate (that would let a censoring builder defeat the IL
    /// by including a same-nonce replacement tx); the BlockProcessor captures this snapshot
    /// before block processing and hands it in, so the tests do the same.
    /// </summary>
    private static Dictionary<AddressAsKey, AccountSnapshot> SnapshotFor(Address sender, ulong balanceWei, UInt256 nonce = default) =>
        new() { [sender] = new AccountSnapshot((UInt256)balanceWei, nonce) };

    [SetUp]
    public void Setup()
    {
        _specProvider = new CustomSpecProvider(((ForkActivation)0, Bogota.Instance));
        _inclusionListValidator = new InclusionListValidator(_specProvider);

        _validTx = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(10.GWei)
            .WithNonce(0)
            .WithValue(1.Ether)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
    }

    [Test]
    public void When_block_full_then_accept()
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(30_000_000)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.True);
    }

    [Test]
    public void When_all_inclusion_list_txs_included_then_accept()
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions(_validTx)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.True);
    }

    [Test]
    public void When_valid_tx_excluded_then_reject()
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.False);
    }

    [Test]
    public void When_no_inclusion_list_then_reject()
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, new Dictionary<AddressAsKey, AccountSnapshot>()), Is.False);
    }

    [Test]
    public void When_il_disabled_by_spec_then_accept_even_if_excluded()
    {
        ISpecProvider prePragueProvider = new CustomSpecProvider(((ForkActivation)0, Prague.Instance));
        InclusionListValidator preBogotaValidator = new(prePragueProvider);

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        Assert.That(preBogotaValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.True);
    }

    [Test]
    public void When_il_tx_sender_lacks_balance_then_accept()
    {
        Transaction expensiveTx = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(10.GWei)
            .WithNonce(0)
            .WithValue(100.Ether) // sender (AddressA) only has 10 ether
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithInclusionListTransactions([expensiveTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.True);
    }

    [Test]
    public void When_il_tx_has_wrong_nonce_then_accept()
    {
        Transaction futureNonceTx = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(10.GWei)
            .WithNonce(5) // sender's actual nonce is 0
            .WithValue(1.Ether)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithInclusionListTransactions([futureNonceTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.True);
    }

    [Test]
    public void When_il_tx_gas_price_below_base_fee_then_accept()
    {
        Transaction lowGasPriceTx = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(1.GWei)
            .WithNonce(0)
            .WithValue(1.Ether)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithBaseFeePerGas(5.GWei)
            .WithInclusionListTransactions([lowGasPriceTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.True);
    }

    [Test]
    public void When_il_tx_gas_limit_exceeds_remaining_block_gas_then_accept()
    {
        Transaction wideTx = Build.A.Transaction
            .WithGasLimit(25_000_000)
            .WithGasPrice(10.GWei)
            .WithNonce(0)
            .WithValue(1.Ether)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(10_000_000) // only 20M remaining, tx needs 25M
            .WithInclusionListTransactions([wideTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.True);
    }

    [Test]
    public void When_partial_il_satisfied_and_remainder_invalid_then_accept()
    {
        // Mix: one IL tx that's included in the block, one that's invalid (wrong nonce).
        // Both conditions absolve the builder per FOCIL conditional-inclusion semantics.
        Transaction invalidTx = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(10.GWei)
            .WithNonce(7)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions(_validTx)
            .WithInclusionListTransactions([_validTx, invalidTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.True);
    }

    [Test]
    public void When_empty_il_then_accept()
    {
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithInclusionListTransactions([])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, new Dictionary<AddressAsKey, AccountSnapshot>()), Is.True);
    }

    /// <summary>
    /// Censorship regression: a builder includes a non-IL transaction `N` from the IL
    /// sender with the same nonce as the IL tx `T`. Under post-execution-state validation
    /// the sender's nonce advances past T.nonce, making T look "not appendable" and the IL
    /// "satisfied" — the exact attack EIP-7805 is designed to prevent. With parent-state
    /// validation T is still appendable so the IL must be reported as unsatisfied.
    /// </summary>
    [Test]
    public void Same_nonce_replacement_tx_does_not_let_builder_skip_il_tx()
    {
        // `_validTx` is from AddressA, nonce=0. The "replacement" is a different tx also from
        // AddressA at nonce=0; only the replacement is in the block, while the IL contains
        // `_validTx`. The validator must reject the block as INVALID_INCLUSION_LIST.
        Transaction replacement = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(10.GWei)
            .WithNonce(0)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressC)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions(replacement)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.False);
    }

    /// <summary>
    /// EIP-1559 (type-2) regression: with `MaxFeePerGas >= baseFee` but
    /// `maxPriorityFeePerGas (a.k.a. tx.GasPrice) &lt; baseFee`, the tx IS valid per
    /// TransactionProcessor's fee check, so the IL validator MUST treat it as appendable.
    /// The earlier code compared `tx.GasPrice` to baseFee and wrongly classified this tx as
    /// "not appendable", letting a builder skip it without triggering INVALID_INCLUSION_LIST.
    /// </summary>
    [Test]
    public void Eip1559_tx_with_low_tip_but_sufficient_fee_cap_is_appendable()
    {
        Transaction eip1559Tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithGasLimit(100_000)
            .WithMaxPriorityFeePerGas(1.GWei)   // tip below baseFee
            .WithMaxFeePerGas(10.GWei)          // cap above baseFee → valid
            .WithNonce(0)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithBaseFeePerGas(5.GWei)
            .WithInclusionListTransactions([eip1559Tx])
            .TestObject;

        Assert.That(_inclusionListValidator.ValidateInclusionList(block, SnapshotFor(TestItem.AddressA, TenEther)), Is.False);
    }
}
