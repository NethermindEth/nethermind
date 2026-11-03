// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Store;

namespace Paprika.Tests.Store;

public abstract class BasePageTests
{
    protected static unsafe Page AllocPage()
    {
        byte* memory = (byte*)NativeMemory.AlignedAlloc(Page.PageSize, sizeof(long));
        new Span<byte>(memory, Page.PageSize).Clear();
        return new Page(memory);
    }

    /// <summary>
    /// An in-memory <see cref="IBatchContext"/> that mirrors Paprika's internal <c>BatchContextBase</c>.
    /// </summary>
    internal class TestBatchContext(uint batchId, Stack<DbAddress>? reusable = null) : IBatchContext
    {
        private const uint StartAddress = 1U;
        private readonly Dictionary<DbAddress, Page> _address2Page = [];
        private readonly Dictionary<UIntPtr, DbAddress> _page2Address = [];
        private readonly Stack<DbAddress> _reusable = reusable ?? [];
        private readonly HashSet<DbAddress> _toReuse = [];

        // data pages should start at non-null addresses
        // 0-N is take by metadata pages
        private uint _pageCount = StartAddress;

        public uint BatchId { get; } = batchId;

        public Page GetAt(DbAddress address) => _address2Page[address];

        public void Prefetch(DbAddress address)
        { }

        public DbAddress GetAddress(Page page) => _page2Address[page.Raw];

        public Page GetNewPage(out DbAddress addr, bool clear)
        {
            Page page;
            if (_reusable.TryPop(out addr))
            {
                page = GetAt(addr);
            }
            else
            {
                page = AllocPage();
                addr = DbAddress.Page(_pageCount++);

                _address2Page[addr] = page;
                _page2Address[page.Raw] = addr;
            }

            if (clear)
                page.Clear();

            page.Header.BatchId = BatchId;

            return page;
        }

        public Page GetWritableCopy(Page page)
        {
            if (page.Header.BatchId == BatchId)
                return page;

            Page writableCopy = GetNewPage(out _, false);
            page.CopyTo(writableCopy);
            AssignBatchId(writableCopy);

            // register as the last because registering can amend the tracking
            RegisterForFutureReuse(page);

            return writableCopy;
        }

        public bool WasWritten(DbAddress addr) => GetAt(addr).Header.BatchId == BatchId;

        public void RegisterForFutureReuse(Page page, bool possibleImmediateReuse = false)
        {
            DbAddress addr = GetAddress(page);
            Assert.That(_toReuse.Add(addr), Is.True, $"Page at {addr} should not be registered as reusable before");
        }

        public void NoticeAbandonedPageReused(Page page) { }

        public IDictionary<Keccak, ContractId> IdCache { get; } = new Dictionary<Keccak, ContractId>();

        public void AssignBatchId(Page page)
        {
            page.Header.BatchId = BatchId;
            page.Header.PaprikaVersion = PageHeader.CurrentVersion;
        }

        public Page TryGetPageAlloc(ref DbAddress addr, PageType pageType)
        {
            Page page;
            if (addr.IsNull)
            {
                page = GetNewPage(out addr, true);
                page.Header.PageType = pageType;
            }
            else
            {
                page = GetAt(addr);
            }

            return page;
        }

        public override string ToString() => $"Batch context used {_pageCount} pages to write the data";

        public TestBatchContext Next()
        {
            HashSet<DbAddress> set = [];

            // push these to reuse
            foreach (DbAddress addr in _toReuse)
            {
                Assert.That(set.Add(addr), Is.True);
            }

            // push reusable leftovers from this
            foreach (DbAddress addr in _reusable)
            {
                Assert.That(set.Add(addr), Is.True);
            }

            TestBatchContext next = new(BatchId + 1, new Stack<DbAddress>(set));

            // remember the mapping
            foreach ((DbAddress addr, Page page) in _address2Page)
            {
                next._address2Page[addr] = page;
            }

            foreach ((UIntPtr page, DbAddress addr) in _page2Address)
            {
                next._page2Address[page] = addr;
            }

            // copy the page count so that it properly allocates new ones
            next._pageCount = _pageCount;

            return next;
        }

        public uint PageCount => _pageCount - StartAddress;
    }

    internal static TestBatchContext NewBatch(uint batchId) => new(batchId);
}
