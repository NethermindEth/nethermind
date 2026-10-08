// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>
/// A managed-nonce reservation resumes where the previous one stopped until the pool reports a removal for the
/// sender, so every way a pooled transaction can stop being reported must hand its nonce back to the next reservation.
/// </summary>
public partial class TxPoolTests
{
    [Test]
    public void Reservation_should_reuse_the_nonce_of_a_removed_transaction()
    {
        NonceManager nonces = CreateReservingPool();
        Transaction reserved = SubmitReserved(nonces, TestItem.PrivateKeyA, 1, TxHandlingOptions.None);

        _txPool.RemoveTransaction(reserved.Hash);

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(0UL));
    }

    [Test]
    public void Reservation_should_reuse_the_nonce_of_an_evicted_transaction()
    {
        NonceManager nonces = CreateReservingPool();
        Transaction reserved = SubmitReserved(nonces, TestItem.PrivateKeyA, 1, TxHandlingOptions.None);

        Assert.That(_txPool.EvictTransaction(reserved), Is.True, "precondition: the transaction is evicted");

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(0UL));
    }

    [Test]
    public void Reservation_should_reuse_the_nonce_of_a_transaction_pushed_out_by_a_better_one()
    {
        NonceManager nonces = CreateReservingPool(new TxPoolConfig { Size = 1 });
        Transaction reserved = SubmitReserved(nonces, TestItem.PrivateKeyA, 1, TxHandlingOptions.None);

        Transaction better = GetTransaction(0, GasCostOf.Transaction, 10, TestItem.AddressF, [], TestItem.PrivateKeyB);
        EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
        Assert.That(_txPool.SubmitTx(better, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(_txPool.ContainsTx(reserved.Hash!, reserved.Type), Is.False, "precondition: the capacity eviction took the reserved transaction");

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(0UL));
    }

    [Test]
    public async Task Reservation_should_reuse_the_nonce_of_a_transaction_dropped_on_a_new_head()
    {
        NonceManager nonces = CreateReservingPool();
        Transaction reserved = SubmitReserved(nonces, TestItem.PrivateKeyA, 1, TxHandlingOptions.None);

        EnsureSenderBalance(TestItem.AddressA, UInt256.Zero);
        await AddEmptyBlock();
        AssertRevalidatedForHead();
        Assert.That(_txPool.ContainsTx(reserved.Hash!, reserved.Type), Is.False, "precondition: the head update dropped the unfunded transaction");

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(0UL));
    }

    [Test]
    public async Task Reservation_should_reuse_the_nonce_of_a_transaction_removed_by_inclusion()
    {
        NonceManager nonces = CreateReservingPool();
        Transaction reserved = SubmitReserved(nonces, TestItem.PrivateKeyA, 1, TxHandlingOptions.PersistentBroadcast);

        await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithTransactions(reserved).TestObject);
        Assert.That(_txPool.ContainsTx(reserved.Hash!, reserved.Type), Is.False, "precondition: inclusion removed the transaction");

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(0UL), "the account nonce is left unchanged so only the removal can free it");
    }

    [Test]
    public void Reservation_should_keep_a_replaced_nonce_taken_until_the_replacement_leaves()
    {
        NonceManager nonces = CreateReservingPool();
        Transaction reserved = SubmitReserved(nonces, TestItem.PrivateKeyA, 1, TxHandlingOptions.None);

        Transaction replacement = GetTransaction(reserved.Nonce, GasCostOf.Transaction, 100, TestItem.AddressF, [], TestItem.PrivateKeyA);
        Assert.That(_txPool.SubmitTx(replacement, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(1UL));

        _txPool.RemoveTransaction(replacement.Hash);

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(0UL));
    }

    [Test]
    public void Reservation_should_reuse_the_nonce_of_a_removed_blob_transaction()
    {
        NonceManager nonces = CreateReservingPool(new TxPoolConfig { BlobsSupport = BlobsSupportMode.InMemory }, GetCancunSpecProvider());
        EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
        Transaction reserved;
        using (NonceLocker locker = nonces.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            reserved = Build.A.Transaction
                .WithNonce(nonce)
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Assert.That(_txPool.SubmitTx(reserved, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            locker.Accept(reserved);
        }

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(1UL), "precondition: the pooled blob transaction holds its nonce");

        _txPool.RemoveTransaction(reserved.Hash);

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(0UL));
    }

    [Test]
    public void Reservation_should_reuse_the_nonce_of_a_transaction_the_broadcaster_dropped()
    {
        NonceManager nonces = CreateReservingPool(new TxPoolConfig { Size = 1 });
        Transaction reserved = SubmitReserved(nonces, TestItem.PrivateKeyA, 1, TxHandlingOptions.PersistentBroadcast);

        Transaction better = GetTransaction(0, GasCostOf.Transaction, 10, TestItem.AddressF, [], TestItem.PrivateKeyB);
        EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
        Assert.That(_txPool.SubmitTx(better, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(1UL), "precondition: only the broadcaster still holds the nonce");

        Transaction betterLocal = GetTransaction(0, GasCostOf.Transaction, 20, TestItem.AddressF, [], TestItem.PrivateKeyC);
        EnsureSenderBalance(TestItem.AddressC, UInt256.MaxValue);
        Assert.That(_txPool.SubmitTx(betterLocal, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(_txPool.ContainsTx(reserved.Hash!, reserved.Type), Is.False, "precondition: the broadcaster's capacity eviction took the reserved transaction");

        Assert.That(Reserve(nonces, TestItem.AddressA), Is.EqualTo(0UL));
    }

    private NonceManager CreateReservingPool(ITxPoolConfig config = null, ISpecProvider specProvider = null)
    {
        _txPool = CreatePool(config, specProvider);
        return new NonceManager(_headInfo, Substitute.For<IStateHeaderProvider>(), Substitute.For<IStateReader>());
    }

    /// <remarks>Also reserves once more without accepting, so the next reservation resumes past the submitted nonce
    /// unless the pool reports a removal.</remarks>
    private Transaction SubmitReserved(NonceManager nonces, PrivateKey sender, UInt256 gasPrice, TxHandlingOptions options)
    {
        EnsureSenderBalance(sender.Address, UInt256.MaxValue);
        Transaction transaction;
        using (NonceLocker locker = nonces.ReserveNonce(sender.Address, _txPool, out ulong nonce))
        {
            transaction = GetTransaction(nonce, GasCostOf.Transaction, gasPrice, TestItem.AddressF, [], sender);
            Assert.That(_txPool.SubmitTx(transaction, options), Is.EqualTo(AcceptTxResult.Accepted));
            locker.Accept(transaction);
        }

        Assert.That(Reserve(nonces, sender.Address), Is.EqualTo(transaction.Nonce + 1), "precondition: the pooled transaction holds its nonce");
        return transaction;
    }

    private ulong Reserve(NonceManager nonces, Address sender)
    {
        using NonceLocker locker = nonces.ReserveNonce(sender, _txPool, out ulong nonce);
        return nonce;
    }
}
