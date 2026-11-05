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
    public void Pending_nonce_follows_the_head_and_the_forwarded_transactions_and_keeps_the_entry()
    {
        HeadNonces head = new() { [TestItem.AddressA] = 3 };
        ForwardedNonceTracker tracker = head.CreateTracker();
        tracker.OnForwarded(TestItem.AddressA, 3);
        ulong beforeTheHeadIncludesIt = tracker.GetPendingNonce(TestItem.AddressA);

        head[TestItem.AddressA] = 6;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeTheHeadIncludesIt, Is.EqualTo(4UL));
            Assert.That(tracker.GetPendingNonce(TestItem.AddressA), Is.EqualTo(6UL), "a head nonce past the forwarded transactions wins");
            Assert.That(tracker.GetPendingNonce(TestItem.AddressB), Is.EqualTo(0UL), "an account missing at the head starts at 0");
            Assert.That(tracker.Count, Is.EqualTo(1), "only a send, which holds the lock of the account, drops its entry");
        }
    }

    // A send from sender: it takes the account's lock, fills the next nonce and, when accepted, records it.
    private static async Task<ulong> Send(ForwardedNonceTracker tracker, Address sender, bool accepted = true)
    {
        using IDisposable senderLock = await tracker.LockSenderAsync(sender);
        ulong nonce = tracker.GetNextNonce(sender);
        if (accepted)
            tracker.OnForwarded(sender, nonce);

        return nonce;
    }

    [Test]
    public async Task Pending_nonce_is_one_the_account_had_while_a_forward_comes_and_goes_across_a_reorg([Values] bool dropped)
    {
        ReorgingHead head = new();
        ForwardedNonceTracker tracker = head.Tracker;
        Address sender = TestItem.AddressA;
        // Pending is 1 when the read looks at the entry, 2 once the forward of nonce 1 is recorded, and 2 from then on:
        // first from the entry, then, after a reorg to nonce 0 and back to 2, from the head.
        head.HoldNextHeadLookup();
        Task<ulong> read = Task.Run(() => tracker.GetPendingNonce(sender));
        ulong pending;
        try
        {
            await head.HeadLookupHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(await Send(tracker, sender), Is.EqualTo(1UL));
            head.MoveTo(0);
            head.HoldNextStateRead();
            head.ReleaseHeadLookup.SetResult();
            await head.StateReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (dropped)
            {
                head.MoveTo(2);
                Assert.That((await Send(tracker, sender, accepted: false), tracker.Count), Is.EqualTo((2UL, 0)),
                    "the send fills the head nonce and drops the entry the head reached");
            }

            head.ReleaseStateRead.SetResult();
            pending = await read.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            head.ReleaseAll();
        }

        Assert.That(pending, Is.AnyOf(1UL, 2UL), "the pending nonce was 1 or 2 for the whole read, never 0");
    }

    [Test]
    public async Task Pending_nonce_is_one_the_account_had_while_a_send_drops_the_entry_and_the_head_goes_back()
    {
        ReorgingHead head = new();
        ForwardedNonceTracker tracker = head.Tracker;
        Address sender = TestItem.AddressA;
        await Send(tracker, sender);
        head.MoveTo(3);
        // Pending is 3 (the head past the entry 2), until a reorg to nonce 0 after a send dropped the entry makes it 0.
        head.HoldNextHeadLookup();
        Task<ulong> read = Task.Run(() => tracker.GetPendingNonce(sender));
        ulong pending;
        try
        {
            await head.HeadLookupHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That((await Send(tracker, sender, accepted: false), tracker.Count), Is.EqualTo((3UL, 0)),
                "the send fills the head nonce and drops the entry the head reached");
            head.MoveTo(0);
            head.ReleaseHeadLookup.SetResult();
            pending = await read.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            head.ReleaseAll();
        }

        Assert.That(pending, Is.AnyOf(3UL, 0UL), "the dropped entry 2 is below the head it was dropped at, so it was never pending");
    }

    [Test]
    public async Task Pending_nonce_stays_one_the_account_had_when_the_head_goes_back_after_a_drop([Values] bool readded)
    {
        ReorgingHead head = new();
        head.MoveTo(3);
        ForwardedNonceTracker tracker = head.Tracker;
        Address sender = TestItem.AddressA;
        await Send(tracker, sender);
        head.HoldNextStateRead();
        Task<ulong> read = Task.Run(() => tracker.GetPendingNonce(sender));
        ulong pending;
        try
        {
            await head.StateReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Pending is 4; a send at head 4 drops the entry and is not accepted; a reorg makes it 1, and a new send 2.
            head.MoveTo(4);
            Assert.That(await Send(tracker, sender, accepted: false), Is.EqualTo(4UL));
            head.MoveTo(1);
            if (readded)
                Assert.That(await Send(tracker, sender), Is.EqualTo(1UL));

            head.ReleaseStateRead.SetResult();
            pending = await read.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            head.ReleaseAll();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pending, readded ? Is.AnyOf(4UL, 1UL, 2UL) : Is.AnyOf(4UL, 1UL));
            Assert.That(tracker.Count, Is.EqualTo(readded ? 1 : 0), "a read neither drops nor restores an entry");
            Assert.That(tracker.GetPendingNonce(sender), Is.EqualTo(readded ? 2UL : 1UL), "the dropped entry does not come back");
        }
    }

    [Test]
    public async Task Pending_nonce_is_one_the_account_had_while_a_send_drops_and_records_its_entry()
    {
        ReorgingHead head = new();
        ForwardedNonceTracker tracker = head.Tracker;
        Address sender = TestItem.AddressA;
        await Send(tracker, sender);
        head.HoldNextStateRead();
        Task<ulong> read = Task.Run(() => tracker.GetPendingNonce(sender));
        ulong pending;
        try
        {
            await head.StateReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Pending is 2; the head reaches 2 and a send drops the entry and records nonce 2: pending 3.
            head.MoveTo(2);
            Assert.That(await Send(tracker, sender), Is.EqualTo(2UL));
            head.ReleaseStateRead.SetResult();
            pending = await read.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            head.ReleaseAll();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pending, Is.AnyOf(2UL, 3UL));
            Assert.That(tracker.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Pending_nonce_reports_a_state_failure_although_an_entry_is_kept()
    {
        ReorgingHead head = new();
        ForwardedNonceTracker tracker = head.Tracker;
        await Send(tracker, TestItem.AddressA);
        head.Failure = new InvalidOperationException("The head state cannot be read");

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<InvalidOperationException>(() => tracker.GetPendingNonce(TestItem.AddressA));
            Assert.That(tracker.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void Pending_nonce_read_holds_the_gate_across_the_state_read_once_entries_keep_changing()
    {
        ReorgingHead head = new();
        ForwardedNonceTracker tracker = head.Tracker;
        Address sender = TestItem.AddressA;
        Task? sendDuringTheLastRead = null;
        bool sendWaitedForTheLastRead = false;
        // Every optimistic read sees a send record a forward while it reads the head state, so each must retry.
        head.AfterStateRead = reads =>
        {
            if (reads <= ForwardedNonceTracker.OptimisticPendingReads)
            {
                using (tracker.LockSenderAsync(sender).GetAwaiter().GetResult())
                    tracker.OnForwarded(sender, (ulong)reads);
            }
            else
            {
                // A send during the last read must wait for it: no entry changes while that read reads the head state. A
                // correct read keeps it waiting for good, so the bound only limits how long the test waits to see that.
                sendDuringTheLastRead = Task.Run(async () =>
                {
                    using (await tracker.LockSenderAsync(sender))
                        tracker.OnForwarded(sender, (ulong)reads);
                });
                sendWaitedForTheLastRead = !sendDuringTheLastRead.Wait(TimeSpan.FromMilliseconds(500));
            }
        };

        ulong pending = tracker.GetPendingNonce(sender);
        sendDuringTheLastRead?.Wait(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(head.StateReads, Is.EqualTo(ForwardedNonceTracker.OptimisticPendingReads + 1));
            Assert.That(sendWaitedForTheLastRead, Is.True, "the last read must hold the gate across its state read");
            Assert.That(pending, Is.EqualTo(ForwardedNonceTracker.OptimisticPendingReads + 1UL),
                "the entry the last send before that read recorded, above the head nonce 1");
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
        // wait whose result is ignored, a wait that is not awaited); one that lets it in only later can pass. Asserted before
        // the cleanup below, so that an over-release error there cannot hide the acquisition failure.
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
