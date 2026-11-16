// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class BlockReplayExtensionsTests
{
    [Test]
    public void WithOwnTransactions_copies_keep_type_state_and_hash_and_leave_the_block_untouched()
    {
        Transaction first = Build.A.NamedTransaction("first").WithNonce(7).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction second = Build.A.NamedTransaction("second").WithNonce(8).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction[] originals = [first, second];
        Block block = Build.A.Block.WithTransactions(originals).TestObject;

        Block replay = block.WithOwnTransactions();
        foreach (Transaction copy in replay.Transactions)
        {
            copy.Nonce += 10;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replay.Transactions, Has.Length.EqualTo(originals.Length));
            for (int i = 0; i < originals.Length; i++)
            {
                Transaction original = originals[i];
                Transaction copy = replay.Transactions[i];
                Assert.That(copy, Is.Not.SameAs(original));
                Assert.That(copy, Is.TypeOf<NamedTransaction>());
                Assert.That(((NamedTransaction)copy).Name, Is.EqualTo(((NamedTransaction)original).Name));
                Assert.That(copy.Hash, Is.EqualTo(original.Hash));
                Assert.That(copy.SenderAddress, Is.EqualTo(original.SenderAddress));
                Assert.That(original.Nonce, Is.EqualTo(7UL + (ulong)i), "writing into the copy must not reach the block's transaction");
                Assert.That(block.Transactions[i], Is.SameAs(original));
            }
            Assert.That(replay.Header, Is.SameAs(block.Header));
        }
    }
}
