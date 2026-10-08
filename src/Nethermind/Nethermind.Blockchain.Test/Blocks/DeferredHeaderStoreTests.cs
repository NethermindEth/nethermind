// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeferredHeaderStoreTests
{
    private readonly TestMemDb _headerDb = new();
    private readonly TestMemDb _blockNumberDb = new();
    private readonly StatePersistenceBarrier _barrier = new();
    private DeferredBlockDataWriter _writer = null!;
    private HeaderStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _writer = DeferredWriteTestHelpers.ManualWriter(_barrier);
        _store = new HeaderStore(_headerDb, _blockNumberDb, null, _writer, _barrier);
    }

    [TearDown]
    public Task TearDown() => _writer.DisposeAsync().AsTask();

    // A fresh store over the same databases, proving what is durable independent of the overlay.
    private HeaderStore Reopen() => new(_headerDb, _blockNumberDb);

    private static BlockHeader HeaderNumbered(ulong number) => Build.A.BlockHeader.WithNumber(number).TestObject;

    [Test]
    public void Deferred_insert_is_visible_before_its_write()
    {
        BlockHeader header = HeaderNumbered(7);
        _store.InsertDeferred(header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.Get(header.Hash!, shouldCache: false), Is.SameAs(header));
            Assert.That(_store.Get(header.Hash!, shouldCache: false, blockNumber: header.Number), Is.SameAs(header));
            Assert.That(_store.GetBlockNumber(header.Hash!), Is.EqualTo(header.Number));
            Assert.That(Reopen().Get(header.Hash!, shouldCache: false), Is.Null, "precondition: nothing is written yet");
        }
    }

    [Test]
    public void Deferred_insert_is_durable_and_identical_after_its_write()
    {
        BlockHeader header = HeaderNumbered(7);
        _store.InsertDeferred(header);

        _writer.Pump();

        HeaderStore reopened = Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopened.Get(header.Hash!, shouldCache: false)?.Hash, Is.EqualTo(header.Hash));
            Assert.That(reopened.GetBlockNumber(header.Hash!), Is.EqualTo(header.Number));
        }
    }

    [Test]
    public void Deleted_header_is_not_resurrected_by_its_queued_write()
    {
        BlockHeader header = HeaderNumbered(7);
        _store.InsertDeferred(header);
        _store.Delete(header.Hash!);

        Assert.That(_store.Get(header.Hash!, shouldCache: false), Is.Null, "a deleted header is not readable before the write");
        _writer.Pump();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.Get(header.Hash!, shouldCache: false), Is.Null);
            Assert.That(Reopen().Get(header.Hash!, shouldCache: false), Is.Null);
            Assert.That(Reopen().GetBlockNumber(header.Hash!), Is.Null);
        }
    }

    [Test]
    public void Persistence_barrier_makes_a_deferred_header_durable()
    {
        BlockHeader header = HeaderNumbered(7);
        _store.InsertDeferred(header);

        _barrier.FlushDeferred();

        Assert.That(Reopen().Get(header.Hash!, shouldCache: false)?.Hash, Is.EqualTo(header.Hash));
    }

    [Test]
    public void Insert_writes_at_once()
    {
        BlockHeader header = HeaderNumbered(7);
        _store.Insert(header);

        Assert.That(Reopen().Get(header.Hash!, shouldCache: false)?.Hash, Is.EqualTo(header.Hash));
    }
}
