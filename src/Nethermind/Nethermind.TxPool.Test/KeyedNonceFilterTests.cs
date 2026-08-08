// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool.Filters;
using NUnit.Framework;

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

    private static AcceptTxResult Accept(Transaction tx, TestReadOnlyStateProvider state)
    {
        KeyedNonceFilter filter = new(state);
        TxFilteringState filteringState = new(tx, state);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    /// <remarks>
    /// Also covers the case this filter exists for: the sender's account nonce is <see cref="AccountNonce"/> throughout,
    /// so every accepted case here is one the account-nonce filters would have rejected as "nonce too low".
    /// </remarks>
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
        // [0] aliases the account nonce, so this filter must not answer for it even though the declared
        // sequence (0) does not match the sender's account nonce.
        Assert.That((bool)Accept(KeyedTx([UInt256.Zero], 0), StateWith(0)), Is.True);

    [Test]
    public void Rejects_a_key_set_that_is_not_well_formed() =>
        // Repeated keys are not strictly increasing, so the set has no canonical encoding.
        Assert.That((bool)Accept(KeyedTx([NonceKey, NonceKey], 0), StateWith(0)), Is.False);
}
