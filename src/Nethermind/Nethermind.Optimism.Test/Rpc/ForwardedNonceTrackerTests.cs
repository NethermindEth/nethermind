// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Optimism.Rpc;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

[Parallelizable(ParallelScope.All)]
public class ForwardedNonceTrackerTests
{
    private sealed class HeadNonces
    {
        private readonly Dictionary<Address, ulong> _nonces = [];

        public ulong this[Address account]
        {
            set => _nonces[account] = value;
        }

        public ForwardedNonceTracker CreateTracker(int maxAccounts = ForwardedNonceTracker.DefaultMaxAccounts)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(Build.A.Block.TestObject);
            IStateReader stateReader = Substitute.For<IStateReader>();
            stateReader.TryGetAccount(Arg.Any<BlockHeader?>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(call =>
            {
                bool exists = _nonces.TryGetValue(call.ArgAt<Address>(1), out ulong nonce);
                call[2] = exists ? new AccountStruct(nonce, 0) : default;
                return exists;
            });
            return new ForwardedNonceTracker(blockFinder, stateReader, maxAccounts);
        }
    }

    [Test]
    public void Next_nonce_follows_the_head_and_then_the_forwarded_transactions()
    {
        HeadNonces head = new() { [TestItem.AddressA] = 3 };
        ForwardedNonceTracker tracker = head.CreateTracker();

        ulong fromHead = tracker.GetNextNonce(TestItem.AddressA);
        tracker.OnForwarded(TestItem.AddressA, 3);
        tracker.OnForwarded(TestItem.AddressA, 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromHead, Is.EqualTo(3UL));
            Assert.That(tracker.GetNextNonce(TestItem.AddressA), Is.EqualTo(5UL));
            Assert.That(tracker.GetNextNonce(TestItem.AddressB), Is.EqualTo(0UL), "an account missing at the head starts at 0");
        }
    }

    [Test]
    public void Account_is_forgotten_once_the_head_includes_its_forwarded_transactions()
    {
        HeadNonces head = new() { [TestItem.AddressA] = 0 };
        ForwardedNonceTracker tracker = head.CreateTracker();
        tracker.OnForwarded(TestItem.AddressA, 0);
        Assert.That(tracker.Count, Is.EqualTo(1));

        head[TestItem.AddressA] = 1;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.GetNextNonce(TestItem.AddressA), Is.EqualTo(1UL));
            Assert.That(tracker.Count, Is.EqualTo(0));
        }
    }

    [Test]
    public void Forward_after_a_gap_does_not_move_the_next_nonce()
    {
        HeadNonces head = new() { [TestItem.AddressA] = 1 };
        ForwardedNonceTracker tracker = head.CreateTracker();

        tracker.OnForwarded(TestItem.AddressA, 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.GetNextNonce(TestItem.AddressA), Is.EqualTo(1UL));
            Assert.That(tracker.Count, Is.EqualTo(0));
        }
    }

    [Test]
    public void Tracks_at_most_the_given_number_of_accounts()
    {
        HeadNonces head = new();
        ForwardedNonceTracker tracker = head.CreateTracker(maxAccounts: 2);
        tracker.OnForwarded(TestItem.AddressA, 0);
        tracker.OnForwarded(TestItem.AddressB, 0);

        // The head includes A's transaction, so A makes room for C.
        head[TestItem.AddressA] = 1;
        tracker.OnForwarded(TestItem.AddressC, 0);
        ulong nextOfB = tracker.GetNextNonce(TestItem.AddressB);
        int countAfterC = tracker.Count;

        // Every tracked account waits for a transaction: one of them is forgotten for D.
        tracker.OnForwarded(TestItem.AddressD, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nextOfB, Is.EqualTo(1UL), "an account still waiting for its transaction is kept while another can go");
            Assert.That(countAfterC, Is.EqualTo(2));
            Assert.That(tracker.Count, Is.EqualTo(2));
            Assert.That(tracker.GetNextNonce(TestItem.AddressD), Is.EqualTo(1UL), "the newest forward is tracked");
        }
    }
}
