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
    public void SelectDirtySenders_CapsPerSenderDepth()
    {
        Dictionary<AddressAsKey, Transaction[]> bySender = new()
        {
            [TestItem.AddressA] = BuildSenderTxs(TestItem.PrivateKeyA, count: 20, gasLimit: 21_000),
        };

        Transaction[] selected = MempoolStatePrewarmer.SelectDirtySenders(bySender, [], maxTxPerSender: 16, gasBudget: 30_000_000);

        Assert.That(selected.Length, Is.EqualTo(16), "no more than maxTxPerSender transactions per sender may be selected");
    }

    [Test]
    public void SelectDirtySenders_StopsAtGasBudget()
    {
        Dictionary<AddressAsKey, Transaction[]> bySender = new()
        {
            [TestItem.AddressA] = BuildSenderTxs(TestItem.PrivateKeyA, count: 10, gasLimit: 21_000),
        };

        // Budget fits two full transactions (42_000) but not a third (63_000).
        Transaction[] selected = MempoolStatePrewarmer.SelectDirtySenders(bySender, [], maxTxPerSender: 16, gasBudget: 50_000);

        Assert.That(selected.Length, Is.EqualTo(2), "selection must stop once the next transaction would exceed the gas budget");
    }

    [Test]
    public void SelectDirtySenders_WhenEmpty_ReturnsEmpty()
    {
        Dictionary<AddressAsKey, Transaction[]> empty = [];
        Transaction[] selected = MempoolStatePrewarmer.SelectDirtySenders(empty, [], maxTxPerSender: 16, gasBudget: 30_000_000);

        Assert.That(selected, Is.Empty, "an empty mempool yields no transactions to warm");
    }

    [Test]
    public void SelectDirtySenders_SecondPassSkipsAlreadyWarmedSender()
    {
        Dictionary<AddressAsKey, Transaction[]> bySender = new()
        {
            [TestItem.AddressA] = BuildSenderTxs(TestItem.PrivateKeyA, count: 3, gasLimit: 21_000),
        };
        Dictionary<AddressAsKey, int> warmedPerSender = [];

        Transaction[] firstPass = MempoolStatePrewarmer.SelectDirtySenders(bySender, warmedPerSender, maxTxPerSender: 16, gasBudget: 30_000_000);
        Transaction[] secondPass = MempoolStatePrewarmer.SelectDirtySenders(bySender, warmedPerSender, maxTxPerSender: 16, gasBudget: 30_000_000);

        Assert.That(firstPass.Length, Is.EqualTo(3), "the first pass warms the sender's whole in-cap queue");
        Assert.That(secondPass, Is.Empty, "a sender whose in-cap queue is already fully warmed is skipped on the next pass");
    }

    [Test]
    public void SelectDirtySenders_SecondPassReplaysFullQueueWhenSenderGrows()
    {
        Dictionary<AddressAsKey, int> warmedPerSender = [];

        Dictionary<AddressAsKey, Transaction[]> firstView = new()
        {
            [TestItem.AddressA] = BuildSenderTxs(TestItem.PrivateKeyA, count: 2, gasLimit: 21_000),
        };
        Transaction[] firstPass = MempoolStatePrewarmer.SelectDirtySenders(firstView, warmedPerSender, maxTxPerSender: 16, gasBudget: 30_000_000);

        // A later-nonce transaction arrives for the same sender.
        Dictionary<AddressAsKey, Transaction[]> grownView = new()
        {
            [TestItem.AddressA] = BuildSenderTxs(TestItem.PrivateKeyA, count: 4, gasLimit: 21_000),
        };
        Transaction[] secondPass = MempoolStatePrewarmer.SelectDirtySenders(grownView, warmedPerSender, maxTxPerSender: 16, gasBudget: 30_000_000);

        Assert.That(firstPass.Length, Is.EqualTo(2), "the first pass warms the two known transactions");
        Assert.That(secondPass.Length, Is.EqualTo(4), "when new transactions arrive the sender's full in-cap queue is replayed so predecessors are present");
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
