// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Tracing;

public class TransactionTraceBoundaryTests
{
    private const int BlockLength = 200;
    private static readonly byte[][] Encoded = Encode(BlockLength);

    /// <remarks>Decoded transactions defer their hashes, so the lookup computes them, both serially and in parallel.</remarks>
    [TestCase(0)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(BlockLength - 1)]
    public void IndexOf_finds_the_target_in_every_lookup_window(int target)
    {
        Block block = Build.A.Block.WithTransactions(Decode()).TestObject;
        Hash256 hash = Rlp.Decode<Transaction>(Encoded[target], RlpBehaviors.SkipTypedWrapping).Hash!;

        Assert.That(Boundary(hash).IndexOf(block), Is.EqualTo(target));
    }

    [Test]
    public void IndexOf_returns_minus_one_for_a_transaction_outside_the_block()
    {
        Transaction[] transactions = Decode();
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Boundary(TestItem.KeccakA).IndexOf(block), Is.EqualTo(-1));
            Assert.That(transactions[BlockLength - 1].Hash, Is.Not.Null);
        }
    }

    [Test]
    public void IndexOf_hashes_no_transaction_past_the_window_that_holds_the_target()
    {
        Transaction[] transactions = Decode();
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        Hash256 hash = Rlp.Decode<Transaction>(Encoded[65], RlpBehaviors.SkipTypedWrapping).Hash!;

        Assert.That(Boundary(hash).IndexOf(block), Is.EqualTo(65));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactions[127].IsHashCalculated, Is.True, "the window [64, 128) is hashed together");
            Assert.That(transactions[128].IsHashCalculated, Is.False, "the next window is not reached");
        }
    }

    [TestCase(0)]
    [TestCase(150)]
    [TestCase(BlockLength - 1)]
    public void IndexOf_with_a_known_index_hashes_only_the_target(int target)
    {
        Transaction[] transactions = Decode();
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        Hash256 hash = Rlp.Decode<Transaction>(Encoded[target], RlpBehaviors.SkipTypedWrapping).Hash!;
        TransactionTraceBoundary boundary = (TransactionTraceBoundary)TransactionTraceBoundary.WrapAt(NullBlockTracer.Instance, hash, target);

        int found = boundary.IndexOf(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.EqualTo(target));
            Assert.That(transactions.Count(static tx => tx.IsHashCalculated), Is.EqualTo(1));
        }
    }

    [TestCase(-1)]
    [TestCase(3)]
    [TestCase(BlockLength)]
    public void IndexOf_with_a_wrong_known_index_falls_back_to_the_lookup(int index)
    {
        const int target = 150;
        Block block = Build.A.Block.WithTransactions(Decode()).TestObject;
        Hash256 hash = Rlp.Decode<Transaction>(Encoded[target], RlpBehaviors.SkipTypedWrapping).Hash!;

        Assert.That(((TransactionTraceBoundary)TransactionTraceBoundary.WrapAt(NullBlockTracer.Instance, hash, index)).IndexOf(block), Is.EqualTo(target));
    }

    private static TransactionTraceBoundary Boundary(Hash256 hash) =>
        (TransactionTraceBoundary)TransactionTraceBoundary.Wrap(NullBlockTracer.Instance, hash);

    private static Transaction[] Decode()
    {
        Transaction[] transactions = new Transaction[Encoded.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Rlp.Decode<Transaction>(Encoded[i], RlpBehaviors.SkipTypedWrapping);
        }

        return transactions;
    }

    private static byte[][] Encode(int count)
    {
        byte[][] encoded = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            Transaction transaction = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            encoded[i] = Rlp.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
        }

        return encoded;
    }
}
