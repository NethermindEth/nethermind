// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Paprika.Tests.Store;

public class PagedDbLifecycleTests
{
    private const long Mb = 1024 * 1024;
    private const byte HistoryDepth = 2;
    private const int Seed = 17;

    private static readonly byte[] Value = Bytes.FromHexString("0x2a");

    public enum BatchOp
    {
        SetRaw,
        DeleteByPrefix,
        Clear,
        SetMetadata,
        TryGet,
        Commit
    }

    [Test]
    public async Task Using_a_batch_after_commit_throws([Values] BatchOp op)
    {
        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        using IBatch batch = db.BeginNextBatch();
        batch.SetAccount(Values.Key0, Value);
        await batch.Commit(CommitOptions.FlushDataAndRoot);

        await Assert.ThatAsync(() => Apply(batch, op), Throws.InstanceOf<ObjectDisposedException>());

        static async Task Apply(IBatch batch, BatchOp op)
        {
            switch (op)
            {
                case BatchOp.SetRaw:
                    batch.SetAccount(Values.Key0, Value);
                    break;
                case BatchOp.DeleteByPrefix:
                    batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(Values.Key0), NibblePath.Empty));
                    break;
                case BatchOp.Clear:
                    batch.Clear();
                    break;
                case BatchOp.SetMetadata:
                    batch.SetMetadata(1, Values.Key1);
                    break;
                case BatchOp.TryGet:
                    batch.TryGet(Key.Account(Values.Key0), out _);
                    break;
                case BatchOp.Commit:
                    await batch.Commit(CommitOptions.FlushDataAndRoot);
                    break;
            }
        }
    }

    [Test]
    public async Task Batch_with_a_failed_write_cannot_commit()
    {
        byte[] committed = Bytes.FromHexString("0x01");
        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        await db.SetAccountAndCommit(committed);

        using (IBatch batch = db.BeginNextBatch())
        {
            // The empty key lives in the state root page's own map. The write copies the page, registering the original for
            // reuse while the root still points at it, then overflows the map.
            Assert.That(() => batch.SetRaw(Key.Account(NibblePath.Empty), new byte[3_500]), Throws.InvalidOperationException);
            await Assert.ThatAsync(async () => await batch.Commit(CommitOptions.FlushDataAndRoot), Throws.InvalidOperationException.With.Message.Contains("failed"));
        }

        Assert.That(db.ReadAccount(), Is.EqualTo(committed));

        // The store is not poisoned.
        await db.SetAccountAndCommit(Value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.ReadAccount(), Is.EqualTo(Value));
            Assert.That(TreeShapeVisitor.VisitRoot(db).PagesClaimedTwice, Is.Zero);
        }
    }

    [Test]
    public async Task Commit_failure_poisons_the_store([Values] bool rootWriteFails)
    {
        byte[] committed = Bytes.FromHexString("0x01");
        TestPageManager manager = new(new NativeMemoryPageManager(Mb, HistoryDepth));
        using PagedDb db = new(manager, HistoryDepth);
        await db.SetAccountAndCommit(committed);

        using (IBatch batch = db.BeginNextBatch())
        {
            batch.SetAccount(Values.Key0, Bytes.FromHexString("0x02"));
            manager.FailPageWrites = !rootWriteFails;
            manager.FailRootWrite = rootWriteFails;

            await Assert.ThatAsync(async () => await batch.Commit(CommitOptions.FlushDataAndRoot), Throws.TypeOf<IOException>());

            // A retry would register the abandoned pages again.
            await Assert.ThatAsync(async () => await batch.Commit(CommitOptions.FlushDataAndRoot), Throws.TypeOf<ObjectDisposedException>(), "Retrying the commit");
        }

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => db.BeginNextBatch(), Throws.InvalidOperationException.With.Message.Contains("poisoned"));
            Assert.That(read.ReadAccount(), Is.EqualTo(committed));
        }
    }

    [Test]
    public async Task Disposing_a_reader_twice_keeps_later_readers_isolated()
    {
        // Root copies are pooled in slabs; more readers than a slab makes the pool hand out a copy returned twice.
        const int readerCount = 64;
        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb);

        IReadOnlyBatch disposedTwice = db.BeginReadOnlyBatch();
        disposedTwice.Dispose();
        disposedTwice.Dispose();

        List<IReadOnlyBatch> readers = [];
        for (int i = 0; i < readerCount; i++)
        {
            await db.SetAccountAndCommit(i.ToByteArray());
            readers.Add(db.BeginReadOnlyBatch());
        }

        int[] seen = [.. readers.Select(reader => BinaryPrimitives.ReadInt32BigEndian(reader.ReadAccount()))];
        foreach (IReadOnlyBatch reader in readers)
        {
            reader.Dispose();
        }

        Assert.That(seen, Is.EqualTo(Enumerable.Range(0, readerCount)));
    }

    [Test]
    public async Task Dispose_with_an_open_reader_keeps_it_readable([Values] bool writeBatch)
    {
        TestPageManager manager = new(new NativeMemoryPageManager(Mb, HistoryDepth));
        PagedDb db = new(manager, HistoryDepth);
        await db.SetAccountAndCommit(Value);

        using IReadOnlyBatch open = writeBatch ? db.BeginNextBatch() : db.BeginReadOnlyBatch();
        db.Dispose();

        // Asserted on its own: reading through the open batch after the pages are freed would crash the process.
        Assert.That(manager.Disposed, Is.False, "The pages must stay mapped while a batch is open");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(open.ReadAccount(), Is.EqualTo(Value));
            Assert.That(() => db.BeginReadOnlyBatch(), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => db.BeginNextBatch(), Throws.TypeOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Disposed_store_rejects_reads()
    {
        PagedDb db = PagedDb.NativeMemoryDb(Mb);
        db.Dispose();

        // Each of these reads a root page, which is freed.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => db.NextFreePage, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => db.Megabytes, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => db.GetReaderStats(), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => db.HasState(Values.Key0), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => db.BeginReadOnlyBatch(Values.Key0), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => db.BeginReadOnlyBatchOrLatest(Values.Key0), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => db.VisitRoot(new TreeShapeVisitor()), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => db.Accept(new TreeShapeVisitor()), Throws.TypeOf<ObjectDisposedException>());
        }
    }

    [Test]
    public async Task Disposing_a_write_batch_twice_keeps_the_next_batch_intact()
    {
        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        IBatch first = db.BeginNextBatch();
        first.SetAccount(Values.Key0, Value);
        await first.Commit(CommitOptions.FlushDataAndRoot);
        first.Dispose();

        using (IBatch next = db.BeginNextBatch())
        {
            // The overwrite abandons the first batch's pages; the next batch tracks them in the context the first one returned.
            next.SetAccount(Values.Key0, Bytes.FromHexString("0x2b"));
            first.Dispose();
            await next.Commit(CommitOptions.FlushDataAndRoot);
        }

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
    }

    [Test]
    public async Task Concurrent_writers_never_build_on_a_stale_root()
    {
        const int writerCount = 2;
        const int commitTarget = 2_000;
        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        await db.SetAccountAndCommit(0.ToByteArray());

        int commits = 0;
        bool readerSawDecrease = false;
        using CancellationTokenSource writersDone = new();

        Task reader = Task.Run(ReadWhileWriting);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, writerCount).Select(_ => Task.Run(Write)));
        }
        finally
        {
            await writersDone.CancelAsync();
            await reader;
        }

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            // Every commit increments the counter, so a batch built on a stale root loses an increment.
            Assert.That(BinaryPrimitives.ReadInt32BigEndian(read.ReadAccount()), Is.EqualTo(commits));
            Assert.That(readerSawDecrease, Is.False);
        }

        async Task Write()
        {
            while (Volatile.Read(ref commits) < commitTarget)
            {
                IBatch batch;
                try
                {
                    batch = db.BeginNextBatch();
                }
                catch (Exception exception) when (exception.Message.StartsWith("There is another batch active"))
                {
                    continue;
                }

                using (batch)
                {
                    int counter = BinaryPrimitives.ReadInt32BigEndian(batch.ReadAccount());
                    batch.SetAccount(Values.Key0, (counter + 1).ToByteArray());
                    await batch.Commit(CommitOptions.FlushDataOnly);
                }

                Interlocked.Increment(ref commits);
            }
        }

        void ReadWhileWriting()
        {
            int last = 0;
            while (!writersDone.IsCancellationRequested)
            {
                using IReadOnlyBatch snapshot = db.BeginReadOnlyBatch();
                int counter = BinaryPrimitives.ReadInt32BigEndian(snapshot.ReadAccount());
                readerSawDecrease |= counter < last;
                last = counter;
            }
        }
    }

    [Test]
    public async Task Reused_abandoned_page_is_written_at_commit()
    {
        const int batchCount = 20;
        TestPageManager manager = new(new NativeMemoryPageManager(16 * Mb, HistoryDepth));
        using PagedDb db = new(manager, HistoryDepth);
        Random random = new(Seed);
        Keccak[] accounts = random.NextKeccaks(200);
        List<uint> unwritten = [];

        for (int i = 0; i < batchCount; i++)
        {
            manager.WrittenPages.Clear();
            using (IBatch batch = db.BeginNextBatch())
            {
                foreach (Keccak account in accounts)
                {
                    batch.SetAccount(account, i.ToByteArray());
                }

                await batch.Commit(CommitOptions.FlushDataAndRoot);
            }

            // A page stamped with the committed batch id was changed by that batch, so the commit must write it.
            uint batchId = Math.Max(db.GetAt(DbAddress.Page(0)).Header.BatchId, db.GetAt(DbAddress.Page(1)).Header.BatchId);
            for (uint at = HistoryDepth; at < db.NextFreePage; at++)
            {
                if (db.GetAt(DbAddress.Page(at)).Header.BatchId == batchId && !manager.WrittenPages.Contains(DbAddress.Page(at)))
                {
                    unwritten.Add(at);
                }
            }
        }

        Assert.That(unwritten, Is.Empty);
    }

    [Test]
    public async Task Reader_stats_report_open_readers_and_oldest_pinned_batch()
    {
        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        await db.SetAccountAndCommit(Value);
        (int, uint, uint) withoutReaders = db.GetReaderStats();

        IReadOnlyBatch oldest = db.BeginReadOnlyBatch();
        await db.SetAccountAndCommit(Value);
        using IReadOnlyBatch newer = db.BeginReadOnlyBatch();
        await db.SetAccountAndCommit(Value);
        (int, uint, uint) withBothReaders = db.GetReaderStats();

        oldest.Dispose();
        (int, uint, uint) afterOldestDisposed = db.GetReaderStats();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutReaders, Is.EqualTo((0, 1u, 1u)));
            Assert.That(withBothReaders, Is.EqualTo((2, 1u, 3u)));
            Assert.That(afterOldestDisposed, Is.EqualTo((1, 2u, 3u)));
        }
    }
}
