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
        Transaction original = Build.A.NamedTransaction("subclass").WithNonce(7).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithTransactions(original).TestObject;

        Block replay = block.WithOwnTransactions();
        Transaction copy = replay.Transactions[0];
        copy.Nonce = 9;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy, Is.Not.SameAs(original));
            Assert.That(copy, Is.TypeOf<NamedTransaction>());
            Assert.That(((NamedTransaction)copy).Name, Is.EqualTo("subclass"));
            Assert.That(copy.Hash, Is.EqualTo(original.Hash));
            Assert.That(copy.SenderAddress, Is.EqualTo(original.SenderAddress));
            Assert.That(original.Nonce, Is.EqualTo(7UL), "writing into the copy must not reach the block's transaction");
            Assert.That(block.Transactions[0], Is.SameAs(original));
            Assert.That(replay.Header, Is.SameAs(block.Header));
        }
    }
}
