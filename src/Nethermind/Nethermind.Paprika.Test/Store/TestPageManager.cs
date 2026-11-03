// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Paprika.Store;

namespace Paprika.Tests.Store;

/// <summary>
/// An <see cref="IPageManager"/> decorator that records the pages accessed and written and whether it was disposed, and
/// fails the writes on demand.
/// </summary>
public sealed class TestPageManager(IPageManager decoratedManager) : IPageManager
{
    public bool FailPageWrites { get; set; }

    public bool FailRootWrite { get; set; }

    public HashSet<DbAddress> WrittenPages { get; } = [];

    /// <summary>The raw addresses of the pages <see cref="GetAt"/> and <see cref="GetAtForWriting"/> returned, in order.</summary>
    public ConcurrentQueue<uint> AccessedPages { get; } = new();

    public bool Disposed { get; private set; }

    public int MaxPage => decoratedManager.MaxPage;

    public Page GetAt(DbAddress address)
    {
        AccessedPages.Enqueue(address.Raw);
        return decoratedManager.GetAt(address);
    }

    public void Prefetch(DbAddress address) => decoratedManager.Prefetch(address);

    public DbAddress GetAddress(in Page page) => decoratedManager.GetAddress(page);

    public Page GetAtForWriting(DbAddress address, bool reused)
    {
        AccessedPages.Enqueue(address.Raw);
        return decoratedManager.GetAtForWriting(address, reused);
    }

    public ValueTask WritePages(ICollection<DbAddress> addresses, CommitOptions options)
    {
        ThrowIf(FailPageWrites);
        WrittenPages.UnionWith(addresses);
        return decoratedManager.WritePages(addresses, options);
    }

    public ValueTask WriteRootPage(DbAddress rootPage, CommitOptions options)
    {
        ThrowIf(FailRootWrite);
        return decoratedManager.WriteRootPage(rootPage, options);
    }

    public void Flush() => decoratedManager.Flush();

    public void ForceFlush() => decoratedManager.ForceFlush();

    public void Dispose()
    {
        Disposed = true;
        decoratedManager.Dispose();
    }

    private static void ThrowIf(bool fail)
    {
        if (fail)
        {
            throw new IOException("Injected write failure");
        }
    }
}
