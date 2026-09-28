// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public partial class TxPoolTests
{
    /// <summary>
    /// Local transactions submitted over RPC are held by the pending pool and by the persistent broadcast pool, which
    /// share the instances. Replacements make the pending pool rewrite the gas bottleneck of a sender's later
    /// transactions, which the persistent pool sorts by.
    /// </summary>
    [Test]
    public void Local_tx_spam_with_replacements_does_not_livelock_persistent_broadcast_eviction()
    {
        // The Hoodi preset's TxPool.Size, which also caps the persistent broadcast pool.
        const int capacity = 1024;
        const int senders = 64;
        const int operations = 8_000;
        _txPool = CreatePool(new TxPoolConfig { Size = capacity, GasLimit = TxGasLimit });

        PrivateKey[] keys = TestItem.PrivateKeys[..senders];
        foreach (PrivateKey key in keys)
        {
            EnsureSenderBalance(key.Address, UInt256.MaxValue);
        }

        Random random = new(1);
        ulong[] nextNonce = new ulong[senders];
        Dictionary<(int Sender, ulong Nonce), UInt256> prices = [];
        int submitted = 0;
        int accepted = 0;

        bool completed = CompletesWithin(() =>
        {
            for (int i = 0; i < operations; i++)
            {
                int sender = random.Next(senders);
                ulong nonce;
                UInt256 price;
                if (nextNonce[sender] > 0 && random.Next(4) == 0)
                {
                    // Replace a pending nonce with a fee bump above the 10% the pool requires.
                    nonce = (ulong)random.Next((int)nextNonce[sender]);
                    price = prices[(sender, nonce)] * 12 / 10 + 1;
                }
                else
                {
                    nonce = nextNonce[sender]++;
                    price = random.Next(1, 100).GWei;
                }

                prices[(sender, nonce)] = price;
                Transaction tx = Build.A.Transaction
                    .WithNonce(nonce)
                    .WithGasPrice(price)
                    .SignedAndResolved(_ethereumEcdsa, keys[sender])
                    .TestObject;
                if (_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast) == AcceptTxResult.Accepted) accepted++;
                submitted++;
            }
        }, TimeSpan.FromSeconds(30));

        if (!completed)
        {
            // The livelocked submission holds the pool's locks, so disposing it would hang the teardown too.
            _txPool = null;
        }

        Assert.That(completed, Is.True, $"local tx submission livelocked on submission {submitted + 1} of {operations} ({accepted} accepted before it)");
        Assert.That(_txPool.GetOwnPendingTransactions(), Has.Length.LessThanOrEqualTo(capacity));
    }

    private static bool CompletesWithin(Action action, TimeSpan timeout)
    {
        Exception error = null;
        // A background thread, so a livelocked run fails the test instead of keeping the test host alive.
        System.Threading.Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        })
        { IsBackground = true };
        thread.Start();
        bool completed = thread.Join(timeout);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return completed;
    }
}
