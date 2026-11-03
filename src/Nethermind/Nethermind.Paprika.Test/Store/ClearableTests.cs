// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using NUnit.Framework;
using Paprika.Store;

namespace Paprika.Tests.Store;

/// <summary>
/// Tests for clearable components. Whether they are properly cleared.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public unsafe class ClearableTests : IDisposable
{
    private readonly byte* _page = (byte*)NativeMemory.AlignedAlloc(Page.PageSize, Page.PageSize);

    [Test]
    public void DataPage() => TestPage<DataPage>();

    [Test]
    public void AbandonedPage() => TestPage<AbandonedPage>();

    [Test]
    public void BottomPage() => TestPage<BottomPage>();

    [Test]
    public void StorageFanOut_Level1Page() => TestPage<StorageFanOut.Level1Page>();

    [Test]
    public void StorageFanOut_Level2Page() => TestPage<StorageFanOut.Level2Page>();

    [Test]
    public void DbAddressList_Of4() => TestDbAddressList<DbAddressList.Of4>();

    [Test]
    public void DbAddressList_Of16() => TestDbAddressList<DbAddressList.Of16>();

    [Test]
    public void DbAddressList_Of64() => TestDbAddressList<DbAddressList.Of64>();

    [Test]
    public void DbAddressList_Of256() => TestDbAddressList<DbAddressList.Of256>();

    [Test]
    public void DbAddressList_Of1024() => TestDbAddressList<DbAddressList.Of1024>();

    private static void TestDbAddressList<TList>()
        where TList : struct, DbAddressList.IDbAddressList
    {
        TList list = new();

        int length = TList.Length;

        for (int i = 0; i < length; i++)
        {
            list[i] = DbAddress.Page(1);
        }

        list.Clear();

        for (int i = 0; i < length; i++)
        {
            Assert.That(list[i], Is.EqualTo(DbAddress.Null));
        }
    }

    private void TestPage<TPage>()
        where TPage : struct, IPage<TPage>
    {
        new Page(_page).Span.Fill(0xFF);

        TPage clearable = TPage.Wrap(new Page(_page));
        clearable.Clear();

        Assert.That(clearable.IsClean, Is.True);
    }

    public void Dispose() => NativeMemory.AlignedFree(_page);
}
