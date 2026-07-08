// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixture]
public class MempoolStatePrewarmerTests
{
    [Test]
    public void SelectTransactions_CapsPerSenderDepth()
    {
        Dictionary<AddressAsKey, Transaction[]> bySender = new()
        {
            [TestItem.AddressA] = BuildSenderTxs(TestItem.PrivateKeyA, count: 20, gasLimit: 21_000),
        };

        Transaction[] selected = MempoolStatePrewarmer.SelectTransactions(bySender, maxTxPerSender: 16, gasBudget: 30_000_000);

        Assert.That(selected.Length, Is.EqualTo(16), "no more than maxTxPerSender transactions per sender may be selected");
    }

    [Test]
    public void SelectTransactions_StopsAtGasBudget()
    {
        Dictionary<AddressAsKey, Transaction[]> bySender = new()
        {
            [TestItem.AddressA] = BuildSenderTxs(TestItem.PrivateKeyA, count: 10, gasLimit: 21_000),
        };

        // Budget fits two full transactions (42_000) but not a third (63_000).
        Transaction[] selected = MempoolStatePrewarmer.SelectTransactions(bySender, maxTxPerSender: 16, gasBudget: 50_000);

        Assert.That(selected.Length, Is.EqualTo(2), "selection must stop once the next transaction would exceed the gas budget");
    }

    [Test]
    public void SelectTransactions_WhenEmpty_ReturnsEmpty()
    {
        Transaction[] selected = MempoolStatePrewarmer.SelectTransactions(new Dictionary<AddressAsKey, Transaction[]>(), maxTxPerSender: 16, gasBudget: 30_000_000);

        Assert.That(selected, Is.Empty, "an empty mempool yields no transactions to warm");
    }

    private static Transaction[] BuildSenderTxs(PrivateKey sender, int count, ulong gasLimit) =>
        Enumerable.Range(0, count)
            .Select(nonce => Build.A.Transaction
                .WithNonce((ulong)nonce)
                .WithGasLimit(gasLimit)
                .WithTo(TestItem.AddressC)
                .SignedAndResolved(sender)
                .TestObject)
            .ToArray();
}
