// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

public class DryRunAttemptsTests
{
    private static readonly Hash256 Head = TestItem.KeccakA;

    private static Transaction Tx(bool carriesBlobs) => new()
    {
        Hash = TestItem.KeccakB,
        BlobVersionedHashes = carriesBlobs ? [[0x01, .. new byte[31]]] : null,
    };

    private static void Fail(DryRunAttempts attempts, Transaction tx, object? source, int times, AcceptTxResult? result = null)
    {
        for (int i = 0; i < times; i++) attempts.Record(Head, tx, source, TxHandlingOptions.None, result ?? AcceptTxResult.FrameSimulationDeferred);
    }

    // A non-blob transaction's hash covers all of it, so another source cannot buy more checks; a blob-carrying one's does not
    // cover the sidecar, so one source's corrupt copy must not use up another's.
    [Test]
    public void Exhausting_one_source_exhausts_another_only_without_blobs([Values] bool carriesBlobs)
    {
        DryRunAttempts attempts = new();
        Transaction tx = Tx(carriesBlobs);
        object first = new(), second = new();

        Fail(attempts, tx, first, DryRunAttempts.MaxPerKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts.IsExhausted(Head, tx, first, TxHandlingOptions.None), Is.True);
            Assert.That(attempts.IsExhausted(Head, tx, second, TxHandlingOptions.None), Is.EqualTo(!carriesBlobs));
        }
    }

    [Test]
    public void Sources_together_cannot_exceed_the_per_transaction_cap()
    {
        DryRunAttempts attempts = new();
        Transaction tx = Tx(carriesBlobs: true);

        for (int i = 0; i < DryRunAttempts.MaxPerTransaction / DryRunAttempts.MaxPerKey; i++) Fail(attempts, tx, new object(), DryRunAttempts.MaxPerKey);

        Assert.That(attempts.IsExhausted(Head, tx, new object(), TxHandlingOptions.None), Is.True);
    }

    // A deferral may clear within the head, so it uses one check; a rejection the content decided uses them all, unless a
    // sidecar the hash does not cover could have decided it.
    [TestCase(false, false, ExpectedResult = false)]
    [TestCase(false, true, ExpectedResult = true)]
    [TestCase(true, true, ExpectedResult = false)]
    public bool One_failure_exhausts_only_a_content_bound_rejection_without_blobs(bool carriesBlobs, bool contentBound)
    {
        DryRunAttempts attempts = new();
        Transaction tx = Tx(carriesBlobs);

        Fail(attempts, tx, null, 1, contentBound ? AcceptTxResult.FrameSimulationFailed : AcceptTxResult.FrameSimulationDeferred);

        return attempts.IsExhausted(Head, tx, null, TxHandlingOptions.None);
    }

    [Test]
    public void A_new_head_resets_the_counts()
    {
        DryRunAttempts attempts = new();
        Transaction tx = Tx(carriesBlobs: false);
        Fail(attempts, tx, null, DryRunAttempts.MaxPerTransaction);

        Assert.That(attempts.IsExhausted(TestItem.KeccakC, tx, null, TxHandlingOptions.None), Is.False);
    }

    [Test]
    public void Local_and_peer_handling_are_counted_apart()
    {
        DryRunAttempts attempts = new();
        Transaction tx = Tx(carriesBlobs: false);
        Fail(attempts, tx, null, DryRunAttempts.MaxPerKey);

        Assert.That(attempts.IsExhausted(Head, tx, null, TxHandlingOptions.PersistentBroadcast), Is.False);
    }
}
