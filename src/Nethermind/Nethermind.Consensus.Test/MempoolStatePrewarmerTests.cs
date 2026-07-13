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
    public void SelectDelta_WhenEmpty_ReturnsEmpty()
    {
        Transaction[] delta = MempoolStatePrewarmer.SelectDelta([], []);

        Assert.That(delta, Is.Empty, "an empty selection yields no transactions to warm");
    }

    [Test]
    public void SelectDelta_FirstPass_SelectsEverySender()
    {
        Transaction[] ordered = [.. BuildSenderTxs(TestItem.PrivateKeyA, 3), .. BuildSenderTxs(TestItem.PrivateKeyB, 2)];
        Dictionary<AddressAsKey, int> warmedPerSender = [];

        Transaction[] delta = MempoolStatePrewarmer.SelectDelta(ordered, warmedPerSender);

        Assert.That(delta.Length, Is.EqualTo(5), "the first pass warms every selected transaction");
        Assert.That(warmedPerSender[TestItem.AddressA], Is.EqualTo(3), "sender A's warmed count is recorded");
        Assert.That(warmedPerSender[TestItem.AddressB], Is.EqualTo(2), "sender B's warmed count is recorded");
    }

    [Test]
    public void SelectDelta_SecondPass_SkipsAlreadyWarmedSenders()
    {
        Transaction[] ordered = [.. BuildSenderTxs(TestItem.PrivateKeyA, 3)];
        Dictionary<AddressAsKey, int> warmedPerSender = [];

        MempoolStatePrewarmer.SelectDelta(ordered, warmedPerSender);
        Transaction[] secondPass = MempoolStatePrewarmer.SelectDelta(ordered, warmedPerSender);

        Assert.That(secondPass, Is.Empty, "a sender whose whole selected set is already warmed is skipped on the next pass");
    }

    [Test]
    public void SelectDelta_SecondPass_ReplaysFullGroupWhenSenderGrows()
    {
        Dictionary<AddressAsKey, int> warmedPerSender = [];

        MempoolStatePrewarmer.SelectDelta(BuildSenderTxs(TestItem.PrivateKeyA, 2), warmedPerSender);
        // A later-nonce transaction arrives for the same sender.
        Transaction[] secondPass = MempoolStatePrewarmer.SelectDelta(BuildSenderTxs(TestItem.PrivateKeyA, 4), warmedPerSender);

        Assert.That(secondPass.Length, Is.EqualTo(4), "when new transactions arrive the sender's full group is replayed so predecessors are present");
    }

    private static Transaction[] BuildSenderTxs(PrivateKey sender, int count) =>
        Enumerable.Range(0, count)
            .Select(nonce => Build.A.Transaction
                .WithNonce((ulong)nonce)
                .WithTo(TestItem.AddressC)
                .SignedAndResolved(sender)
                .TestObject)
            .ToArray();
}
