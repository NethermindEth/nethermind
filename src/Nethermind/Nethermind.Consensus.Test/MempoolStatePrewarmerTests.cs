// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
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

    [Test]
    public void BuildNextBlockHeader_PreservesChainSpecificHeaderSubtype()
    {
        // Regression: the header was previously built via `new BlockHeader(...)`, degrading chain-specific subtypes
        // to a plain BlockHeader and throwing InvalidCastException in chain-specific processors (e.g. XDC).
        ChainSpecificHeader parent = new(
            TestItem.KeccakA, Keccak.OfAnEmptySequenceRlp, TestItem.AddressA, UInt256.One, 10, 30_000_000, 100, [])
        {
            Hash = TestItem.KeccakB,
            MixHash = TestItem.KeccakC,
            ParentBeaconBlockRoot = TestItem.KeccakD,
            BaseFeePerGas = 7,
        };

        BlockHeader next = MempoolStatePrewarmer.BuildNextBlockHeader(parent, timestamp: 200, London.Instance);

        Assert.That(next, Is.InstanceOf<ChainSpecificHeader>(), "chain-specific header subtypes must survive so chain-specific processors don't hit an InvalidCastException");
        Assert.That(next.Number, Is.EqualTo(parent.Number + 1), "the child is the parent's successor");
        Assert.That(next.MixHash, Is.EqualTo(parent.MixHash), "MixHash is propagated from the parent");
        Assert.That(next.ParentBeaconBlockRoot, Is.EqualTo(parent.ParentBeaconBlockRoot), "ParentBeaconBlockRoot is propagated from the parent");
        Assert.That(next.BaseFeePerGas, Is.EqualTo(BaseFeeCalculator.Calculate(parent, London.Instance)), "BaseFeePerGas is recalculated for the child");
    }

    // Stands in for a chain-specific header subtype (e.g. XdcBlockHeader) whose CreateSimulatedChild returns its own type.
    private sealed class ChainSpecificHeader(
        Hash256 parentHash, Hash256 unclesHash, Address beneficiary, in UInt256 difficulty,
        ulong number, ulong gasLimit, ulong timestamp, byte[] extraData)
        : BlockHeader(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData)
    {
        public override BlockHeader CreateSimulatedChild(ulong timestamp) =>
            new ChainSpecificHeader(Hash!, Keccak.OfAnEmptySequenceRlp, Beneficiary!, UInt256.Zero, Number + 1, GasLimit, timestamp, [])
            {
                MixHash = Hash256.Zero,
            };
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
