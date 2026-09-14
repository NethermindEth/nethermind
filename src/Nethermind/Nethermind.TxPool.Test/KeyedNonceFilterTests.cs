// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NUnit.Framework;
using static Nethermind.TxPool.Test.FrameTxFilterTestPools;

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
internal class KeyedNonceFilterTests
{
    private static readonly UInt256 NonceKey = 0xbeef;
    private static readonly Address Sender = TestItem.AddressA;

    /// <summary>The sender's account nonce, deliberately unequal to any sequence under test.</summary>
    private const ulong AccountNonce = 7;

    private static Transaction KeyedTx(UInt256[] nonceKeys, ulong nonceSeq) =>
        Build.A.Transaction
            .WithType(TxType.FrameTx)
            .WithNonce(nonceSeq)
            .WithNonceKeys(nonceKeys)
            .WithSenderAddress(Sender)
            .TestObject;

    private static TestReadOnlyStateProvider StateWith(ulong storedSeq)
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Sender, UInt256.One, AccountNonce);
        if (storedSeq != 0)
        {
            state.Set(KeyedNonceManager.StorageSlot(Sender, NonceKey), ((UInt256)storedSeq).ToBigEndian().WithoutLeadingZeros().ToArray());
        }

        return state;
    }

    private static AcceptTxResult Accept(Transaction tx, TestReadOnlyStateProvider state, ITxPoolConfig? config = null, params Transaction[] pending)
    {
        (TxDistinctSortedPool standard, TxDistinctSortedPool blob) = tx.CarriesBlobs
            ? (Pool(blobs: false), Pool(blobs: true, pending))
            : (Pool(blobs: false, pending), Pool(blobs: true));
        KeyedNonceFilter filter = new(state, config ?? new TxPoolConfig(), standard, blob);
        TxFilteringState filteringState = new(tx, state, Eip8141Prototype.Instance);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    /// <remarks>The sender's account nonce is <see cref="AccountNonce"/> throughout, so every accepted case here
    /// is one the account-nonce filters would have rejected as "nonce too low".</remarks>
    [TestCase(0ul, 0ul, true, TestName = "first use of an unused key")]
    [TestCase(3ul, 3ul, true, TestName = "key at the declared sequence")]
    [TestCase(3ul, 2ul, false, TestName = "sequence already consumed")]
    [TestCase(3ul, 4ul, false, TestName = "sequence not reached yet")]
    public void Admits_a_keyed_set_only_at_its_current_sequence(ulong storedSeq, ulong declaredSeq, bool expectedAccepted)
    {
        AcceptTxResult result = Accept(KeyedTx([NonceKey], declaredSeq), StateWith(storedSeq));

        Assert.That((bool)result, Is.EqualTo(expectedAccepted));
        if (!expectedAccepted)
        {
            Assert.That(result.ToString(), Does.Contain(TxPoolErrorMessages.KeyedNonceUnmet));
        }
    }

    [Test]
    public void Leaves_the_account_nonce_domain_to_the_account_nonce_filters() =>
        Assert.That((bool)Accept(KeyedTx([UInt256.Zero], 0), StateWith(0)), Is.True);

    /// <remarks>The decoder rejects a malformed set before the pool sees it, so this pins the filter's own guard
    /// rather than a reachable ingress path.</remarks>
    [Test]
    public void Rejects_a_key_set_that_is_not_well_formed() =>
        Assert.That((bool)Accept(KeyedTx([NonceKey, NonceKey], 0), StateWith(0)), Is.False);

    /// <remarks>Every fresh key is current at sequence zero, so nothing about the sender's account nonce
    /// bounds how many a funded sender may hold pending; only a count does.</remarks>
    [TestCase(false, TestName = "keyed transactions in the standard pool")]
    [TestCase(true, TestName = "keyed blob transactions against the blob limit")]
    public void Bounds_pending_keyed_transactions_by_the_configured_per_sender_limit(bool carriesBlobs)
    {
        const int limit = 2;
        Transaction[] pending = [FreshKeyTx(1, carriesBlobs), FreshKeyTx(2, carriesBlobs)];

        AcceptTxResult result = Accept(FreshKeyTx(3, carriesBlobs), StateWith(0), LimitOf(limit, carriesBlobs), pending);

        Assert.That(result.ToString(), Does.Contain("too many pending keyed nonce transactions"));
    }

    [TestCase(false, TestName = "keyed transactions in the standard pool")]
    [TestCase(true, TestName = "keyed blob transactions against the blob limit")]
    public void Admits_a_keyed_transaction_displacing_a_pending_one_at_the_limit(bool carriesBlobs)
    {
        const int limit = 2;
        Transaction incumbent = FreshKeyTx(1, carriesBlobs);
        Transaction[] pending = [incumbent, FreshKeyTx(2, carriesBlobs)];

        // Same key and sequence: it takes the incumbent's place rather than adding to the count.
        Transaction replacement = FreshKeyTx(1, carriesBlobs);
        replacement.Hash = TestItem.KeccakH;  // a distinct hash, so only the competing key makes it a replacement

        AcceptTxResult result = Accept(replacement, StateWith(0), LimitOf(limit, carriesBlobs), pending);

        Assert.That((bool)result, Is.True);
    }

    [Test]
    public void Leaves_keyed_transactions_unbounded_when_no_limit_is_configured()
    {
        Transaction[] pending = [FreshKeyTx(1, carriesBlobs: false), FreshKeyTx(2, carriesBlobs: false)];

        AcceptTxResult result = Accept(FreshKeyTx(3, carriesBlobs: false), StateWith(0), LimitOf(0, carriesBlobs: false), pending);

        Assert.That((bool)result, Is.True);
    }

    private static ITxPoolConfig LimitOf(int limit, bool carriesBlobs) => carriesBlobs
        ? new TxPoolConfig { MaxPendingBlobTxsPerSender = limit, MaxPendingTxsPerSender = 0 }
        : new TxPoolConfig { MaxPendingTxsPerSender = limit, MaxPendingBlobTxsPerSender = 0 };

    /// <summary>A keyed frame transaction at sequence zero under its own key, which every account-nonce
    /// filter admits however many the sender already holds.</summary>
    private static Transaction FreshKeyTx(int key, bool carriesBlobs)
    {
        Transaction tx = KeyedTx([(UInt256)key], 0);
        if (carriesBlobs) tx.BlobVersionedHashes = [new byte[32]];
        byte[] hash = new byte[Hash256.Size];
        BinaryPrimitives.WriteInt32BigEndian(hash, key);
        tx.Hash = new Hash256(hash);
        return tx;
    }
}
