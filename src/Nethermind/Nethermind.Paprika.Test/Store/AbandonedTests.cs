// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class AbandonedTests : BasePageTests
{
    [Test]
    public void Page_capacity()
    {
        const int expected = 2000;

        DbAddress[] addresses = Enumerable.Range(1, expected).Select(i => DbAddress.Page((uint)i)).ToArray();

        AbandonedPage page = new(Page.DevOnlyNativeAlloc());
        page.AsPage().Clear();

        Stack<DbAddress> added = [];

        foreach (DbAddress address in addresses)
        {
            if (page.TryPush(address))
            {
                added.Push(address);
            }
        }

        Assert.That(added, Has.Count.EqualTo(expected));

        while (added.TryPop(out DbAddress dequeued))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(page.TryPop(out DbAddress popped), Is.True);
                Assert.That(popped, Is.EqualTo(dequeued));
            }
        }
    }

    [Test]
    public void Properly_handles_page_addresses_that_are_packed_1()
    {
        AbandonedList list = new();

        const uint batchId = 10;

        TestBatchContext batch = new(batchId);
        batch.GetNewPage(out DbAddress a, false);
        batch.GetNewPage(out DbAddress b, false);

        list.Register([a, b], batch);

        TestBatchContext next = batch.Next();

        Assert.That(list.TryGet(out DbAddress oneOfReused, next.BatchId, next), Is.True);

        HashSet<DbAddress> addresses = [a, b];
        Assert.That(addresses.Remove(oneOfReused), Is.True, $"The {oneOfReused} should be in the set");
        DbAddress last = addresses.Single();

        Assert.That(list.GetCurrentForTest(), Is.EqualTo(last));
    }

    [Test]
    public async Task Properly_handles_page_addresses_that_are_packed_2()
    {
        using PagedDb db = PagedDb.NativeMemoryDb(32 * Page.PageSize, 2);

        byte[] value = new byte[1024];

        for (int i = 0; i < 1000; i++)
        {
            using IBatch batch = db.BeginNextBatch();

            // should modify 2 pages per commit
            batch.SetAccount(Keccak.OfAnEmptySequenceRlp, value);
            await batch.Commit(CommitOptions.FlushDataOnly);
        }
    }

    private const int HistoryDepth = 2;

    [TestCase(24, 1, 10_000, false, TestName = "Accounts - 1")]
    [TestCase(764, 100, 10_000, false, TestName = "Accounts - 100")]
    [TestCase(21822, 4000, 200, false,
        TestName = "Accounts - 4000 to get a bit reuse",
        Explicit = true, Reason = "long running")]
    [TestCase(55534, 10_000, 50, false,
        TestName = "Accounts - 10000 to breach the AbandonedPage",
        Explicit = true, Reason = "long running")]
    [TestCase(103345, 20_000, 50, true,
        TestName = "Storage - 20_000 accounts with a single storage slot",
        Explicit = true, Reason = "long running")]
    public async Task Reuse_in_limited_environment(int pageCount, int accounts, int repeats, bool isStorage)
    {
        Keccak[] keccaks = Initialize(accounts);

        // set big value
        byte[] accountValue = new byte[2900];
        new Random(17).NextBytes(accountValue);

        using PagedDb db = PagedDb.NativeMemoryDb(pageCount * Page.PageSize, HistoryDepth);

        for (int i = 0; i < repeats; i++)
        {
            using IBatch block = db.BeginNextBatch();
            foreach (Keccak keccak in keccaks)
            {
                if (isStorage)
                {
                    block.SetStorage(keccak, keccak, accountValue);
                }
                else
                {
                    block.SetAccount(keccak, accountValue);
                }
            }

            await block.Commit(CommitOptions.FlushDataAndRoot);
        }

        Assert.That(db.NextFreePage, Is.EqualTo((uint)pageCount),
            "Ensure that the page count is minimal. " +
            "After running the test they should mach the allocated space.");

        List<uint> oldPages = [];

        for (uint at = 0; at < db.NextFreePage; at++)
        {
            Page page = db.GetAt(DbAddress.Page(at));
            if (page.Header.BatchId < (uint)(repeats - HistoryDepth - 1))
            {
                oldPages.Add(at);
            }
        }

        if (oldPages.Count > 0)
        {
            int[] counters = new int[byte.MaxValue];

            Dictionary<uint, uint> ages = [];

            foreach (uint addr in oldPages)
            {
                Page page = db.GetAt(DbAddress.Page(addr));

                if (page.Header.PageType == PageType.Abandoned)
                {
                    ages[addr] = page.Header.BatchId;
                }

                counters[(int)page.Header.PageType]++;
            }

            Console.WriteLine("Abandoned addr->batch: ");
            foreach ((uint addr, uint batchId) in ages)
            {
                Console.WriteLine($"  @{addr}: {batchId}");
            }

            foreach (PageType type in Enum.GetValues<PageType>())
            {
                Console.WriteLine($"{type}: {counters[(int)type]}");
            }
        }
    }

    [Test]
    public async Task Work_proper_bookkeeping_when_lots_of_reads()
    {
        const int repeats = 1_000;
        const int multiplier = 3 + 1; // fanout page + data page + abandoned page per commit
        const int historyDepth = 2;

        Keccak account = Keccak.EmptyTreeHash;

        byte[] value = Bytes.FromHexString("0x0d");

        using PagedDb db = PagedDb.NativeMemoryDb((multiplier * repeats + historyDepth) * Page.PageSize);

        List<IReadOnlyBatch> reads = [];

        for (int i = 0; i < repeats; i++)
        {
            reads.Add(db.BeginReadOnlyBatch());

            using IBatch block = db.BeginNextBatch();
            block.SetAccount(account, value);
            block.VerifyDbPagesOnCommit();

            await block.Commit(CommitOptions.FlushDataAndRoot);
        }

        foreach (IReadOnlyBatch read in reads)
        {
            read.Dispose();
        }
    }

    [Test]
    [Explicit("long running")]
    public async Task Reuse_in_grow_and_shrink()
    {
        const int repeats = 200_000;
        const int spikeEvery = 100;
        const int spikeSize = 1000;

        Keccak account = Keccak.EmptyTreeHash;
        Keccak[] spikeAccounts = new Keccak[spikeSize];

        new Random(13).NextBytes(MemoryMarshal.Cast<Keccak, byte>(spikeAccounts.AsSpan()));

        byte[] value = Bytes.FromHexString("0x0d");

        using PagedDb db = PagedDb.NativeMemoryDb(4096 * Page.PageSize);

        for (int i = 0; i < repeats; i++)
        {
            using IBatch block = db.BeginNextBatch();
            block.SetAccount(account, value);

            if (i % spikeEvery == 0)
            {
                // spike set
                foreach (Keccak spikeAccount in spikeAccounts)
                {
                    block.SetAccount(spikeAccount, value);
                }
            }
            else if (i % spikeEvery == 1)
            {
                // spike delete
                foreach (Keccak spikeAccount in spikeAccounts)
                {
                    block.SetAccount(spikeAccount, ReadOnlySpan<byte>.Empty);
                }
            }

            await block.Commit(CommitOptions.FlushDataAndRoot);
        }
    }

    [Test]
    public async Task Abandoned_chain_creation_with_overflow()
    {
        const int delta = 10;

        // Minimum iterations required to overflow the abandoned list.
        const int minIterations = AbandonedList.MaxCount + delta;

        // Minimum accounts required to overflow an abandoned page.
        int numAccounts = AbandonedPage.MaxCount * 2 + delta;

        Keccak[] keccaks = Initialize(numAccounts);

        // Set big value.
        byte[] accountValue = new byte[2900];
        new Random(17).NextBytes(accountValue);

        using PagedDb db = PagedDb.NativeMemoryDb(350_000 * Page.PageSize, HistoryDepth);

        // Ensure that AbandonedList can pack maximum number of abandoned page addresses.
        const int estimatedMetadataSize = 32;
        Assert.That(AbandonedList.MaxCount, Is.GreaterThan(AbandonedList.Size / DbAddress.Size - estimatedMetadataSize));

        // Start read only batch to ensure that new pages are allocated instead of reusing
        // the abandoned pages.
        using (db.BeginReadOnlyBatch())
        {
            for (int i = 0; i < minIterations; i++)
            {
                using IBatch block = db.BeginNextBatch();
                foreach (Keccak keccak in keccaks)
                {
                    block.SetAccount(keccak, accountValue);
                }

                block.VerifyDbPagesOnCommit();
                await block.Commit(CommitOptions.FlushDataAndRoot);
            }
        }

        // Try reusing the abandoned pages.
        for (int i = 0; i < minIterations; i++)
        {
            using IBatch block = db.BeginNextBatch();
            foreach (Keccak keccak in keccaks)
            {
                block.SetAccount(keccak, accountValue);
            }

            block.VerifyDbPagesOnCommit();
            await block.Commit(CommitOptions.FlushDataAndRoot);
        }
    }

    private static Keccak[] Initialize(int accounts)
    {
        Keccak[] keccaks = new Keccak[accounts];
        const int seed = 13;
        new Random(seed).NextBytes(MemoryMarshal.Cast<Keccak, byte>(keccaks.AsSpan()));
        return keccaks;
    }
}
