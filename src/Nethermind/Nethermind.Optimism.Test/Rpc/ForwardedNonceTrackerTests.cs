// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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

        public ForwardedNonceTracker CreateTracker()
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
            return new ForwardedNonceTracker(blockFinder, stateReader);
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
    public async Task Sender_lock_holds_only_its_account_and_goes_once_unused()
    {
        ForwardedNonceTracker tracker = new HeadNonces().CreateTracker();

        IDisposable first = await tracker.LockSenderAsync(TestItem.AddressA);
        Task<IDisposable> sameAccount = tracker.LockSenderAsync(TestItem.AddressA);
        Task<IDisposable> otherAccount = tracker.LockSenderAsync(TestItem.AddressB);
        bool otherAccountWaited = !otherAccount.IsCompleted;
        int locksWhileHeld = tracker.SenderLockCount;
        // A correct lock never lets the second send in while the first holds it, so this window cannot fail one. It catches a
        // lock that lets the second in on its own within 2 s, above the 1 s of the bypasses it was written against (a timed
        // wait whose result is ignored, a wait that is not awaited); one that lets it in only later passes. Checked at once,
        // as such a lock cannot be released cleanly below.
        await Task.WhenAny(sameAccount, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.That(sameAccount.IsCompleted, Is.False, "a second send from the account waits for the first");

        first.Dispose();
        IDisposable second = await sameAccount.WaitAsync(TimeSpan.FromSeconds(10));
        (int Users, bool Held) afterFirst = tracker.SenderLockState(TestItem.AddressA);
        second.Dispose();
        (await otherAccount).Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherAccountWaited, Is.False, "a send from another account does not wait");
            Assert.That(locksWhileHeld, Is.EqualTo(2));
            Assert.That(afterFirst, Is.EqualTo((1, true)), "the second send holds the lock once the first is done");
            Assert.That(tracker.SenderLockCount, Is.EqualTo(0), "a lock no send uses is removed");
        }
    }

    [Test]
    public async Task Sender_lock_stays_while_a_send_waits_on_it()
    {
        ForwardedNonceTracker tracker = new HeadNonces().CreateTracker();

        IDisposable first = await tracker.LockSenderAsync(TestItem.AddressA);
        Task<IDisposable> second = tracker.LockSenderAsync(TestItem.AddressA);
        first.Dispose();
        IDisposable secondHeld = await second.WaitAsync(TimeSpan.FromSeconds(10));
        // The second send holds the lock now, so a third one from the account must wait for it.
        Task<IDisposable> third = tracker.LockSenderAsync(TestItem.AddressA);
        bool thirdWaited = !third.IsCompleted;
        secondHeld.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thirdWaited, Is.True, "a lock must not be removed while a send still waits on it");
            Assert.That(tracker.SenderLockCount, Is.EqualTo(0), "a lock no send uses is removed");
        }
    }

    [Test]
    public void Forwards_from_many_accounts_at_once_are_all_tracked()
    {
        const int accounts = 4096;
        // No head: every account starts at nonce 0, without a state read.
        ForwardedNonceTracker tracker = new(Substitute.For<IBlockFinder>(), Substitute.For<IStateReader>());
        Address[] senders = Enumerable.Range(1, accounts).Select(static i => Address.FromNumber((UInt256)i)).ToArray();

        Parallel.ForEach(senders, new ParallelOptions { MaxDegreeOfParallelism = 8 }, sender =>
        {
            tracker.OnForwarded(sender, 0);
            tracker.OnForwarded(sender, 1);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.Count, Is.EqualTo(accounts));
            Assert.That(senders.Count(sender => tracker.GetNextNonce(sender) != 2), Is.EqualTo(0), "every account must be at nonce 2");
        }
    }
}
