// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class SnapshotBundleOwnerReadCacheTests
{
    private static readonly Address Address = TestItem.AddressA;
    private static readonly Account StoredAccount = TestItem.GenerateIndexedAccount(1);

    [Test]
    public void PromoteAccount_OnTheOpeningThread_ServesItsLaterReadsWithoutTheSharedCache()
    {
        IPersistence.IPersistenceReader reader = Reader();
        using SnapshotBundle bundle = Bundle(reader, cacheOwnerReadsWithoutLocks: true);

        bundle.PromoteAccount(Address, bundle.GetAccount(Address));
        Account? ownerRead = bundle.GetAccount(Address, out bool ownerHit);
        bool otherThreadHit = IsCachedForAnotherThread(bundle);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ownerRead, Is.EqualTo(StoredAccount));
            Assert.That(ownerHit, Is.True, "the opening thread reads its own promotion");
            Assert.That(otherThreadHit, Is.False, "the promotion stays out of the cache that other threads share");
            reader.Received(2).GetAccount(Address);
        }
    }

    [Test]
    public void PromoteAccount_WithoutAnOwner_UsesTheSharedCache()
    {
        IPersistence.IPersistenceReader reader = Reader();
        using SnapshotBundle bundle = Bundle(reader, cacheOwnerReadsWithoutLocks: false);

        bundle.PromoteAccount(Address, bundle.GetAccount(Address));
        bool otherThreadHit = IsCachedForAnotherThread(bundle);

        Assert.That(otherThreadHit, Is.True);
    }

    [Test]
    public void PromoteAccount_FromAnotherThread_IsSeenByEveryThread()
    {
        IPersistence.IPersistenceReader reader = Reader();
        using SnapshotBundle bundle = Bundle(reader, cacheOwnerReadsWithoutLocks: true);

        OnAnotherThread(() => bundle.PromoteAccount(Address, StoredAccount));
        bundle.GetAccount(Address, out bool ownerHit);
        bool otherThreadHit = IsCachedForAnotherThread(bundle);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ownerHit, Is.True);
            Assert.That(otherThreadHit, Is.True);
            reader.DidNotReceive().GetAccount(Address);
        }
    }

    [Test]
    public void PromoteAccount_OfAWrittenAccount_DoesNotHideTheWrite()
    {
        using SnapshotBundle bundle = Bundle(Reader(), cacheOwnerReadsWithoutLocks: true);
        Account written = TestItem.GenerateIndexedAccount(2);

        bundle.SetAccount(Address, written);
        bundle.PromoteAccount(Address, StoredAccount);

        Assert.That(bundle.GetAccount(Address), Is.EqualTo(written));
    }

    [Test]
    public void CollectAndApplySnapshot_DropsTheOwnerCache()
    {
        IPersistence.IPersistenceReader reader = Reader();
        using SnapshotBundle bundle = Bundle(reader, cacheOwnerReadsWithoutLocks: true);
        bundle.PromoteAccount(Address, bundle.GetAccount(Address));

        (Snapshot? snapshot, TransientResource? resource) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, Keccak.EmptyTreeHash));
        snapshot!.Dispose();
        resource!.ReleaseLease();
        Account? afterCommit = bundle.GetAccount(Address, out bool hit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterCommit, Is.EqualTo(StoredAccount));
            Assert.That(hit, Is.False, "a read cached before the commit must be read from below again");
            reader.Received(2).GetAccount(Address);
        }
    }

    // A thread of its own: a pool task waited on can run inline on the waiting thread.
    private static void OnAnotherThread(Action action)
    {
        Thread thread = new(() => action());
        thread.Start();
        thread.Join();
    }

    private static bool IsCachedForAnotherThread(SnapshotBundle bundle)
    {
        bool hit = false;
        OnAnotherThread(() => bundle.GetAccount(Address, out hit));
        return hit;
    }

    private static IPersistence.IPersistenceReader Reader()
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.GetAccount(Address).Returns(StoredAccount);
        return reader;
    }

    private static SnapshotBundle Bundle(IPersistence.IPersistenceReader reader, bool cacheOwnerReadsWithoutLocks) =>
        new(new ReadOnlySnapshotBundle(new SnapshotPooledList(0), reader, false, PersistedSnapshotStack.Empty()),
            Substitute.For<ITrieNodeCache>(),
            new ResourcePool(new FlatDbConfig()),
            ResourcePool.Usage.ReadOnlyProcessingEnv,
            cacheOwnerReadsWithoutLocks: cacheOwnerReadsWithoutLocks);
}
