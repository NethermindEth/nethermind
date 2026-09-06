// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class DoubleEndedLruTests
{
    [Test]
    public void AddOrRefresh_ReturnsAddedUntilCapacity_ThenFull()
    {
        DoubleEndedLru<string, string> lru = new(2);

        Assert.That(lru.AddOrRefresh("a", "1"), Is.EqualTo(BucketAddResult.Added));
        Assert.That(lru.AddOrRefresh("b", "2"), Is.EqualTo(BucketAddResult.Added));
        Assert.That(lru.AddOrRefresh("c", "3"), Is.EqualTo(BucketAddResult.Full));
        Assert.That(lru.Count, Is.EqualTo(2));
        Assert.That(lru.Contains("c"), Is.False);
    }

    [Test]
    public void AddOrRefresh_RefreshMovesEntryToHeadAndReturnsPreviousValue()
    {
        DoubleEndedLru<string, string> lru = new(3);
        lru.AddOrRefresh("a", "1");
        lru.AddOrRefresh("b", "2");
        lru.AddOrRefresh("c", "3");

        Assert.That(lru.AddOrRefresh("a", "1b", out string? previous), Is.EqualTo(BucketAddResult.Refreshed));

        Assert.That(previous, Is.EqualTo("1"));
        Assert.That(lru.GetByKey("a"), Is.EqualTo("1b"));
        Assert.That(lru.GetAll(), Is.EqualTo(new[] { "1b", "3", "2" }));
        Assert.That(lru.GetAllWithKey(), Is.EqualTo(new[] { ("a", "1b"), ("c", "3"), ("b", "2") }));
    }

    [Test]
    public void TryPopHead_RemovesMostRecentEntry()
    {
        DoubleEndedLru<string, string> lru = new(3);
        lru.AddOrRefresh("a", "1");
        lru.AddOrRefresh("b", "2");

        Assert.That(lru.TryPopHead(out string key, out string? value), Is.True);
        Assert.That((key, value), Is.EqualTo(("b", "2")));
        Assert.That(lru.GetAll(), Is.EqualTo(new[] { "1" }));

        Assert.That(lru.TryPopHead(out _, out _), Is.True);
        Assert.That(lru.TryPopHead(out _, out _), Is.False);
    }

    [Test]
    public void TryGetLast_ReturnsLeastRecentEntry()
    {
        DoubleEndedLru<string, string> lru = new(3);
        Assert.That(lru.TryGetLast(out _), Is.False);

        lru.AddOrRefresh("a", "1");
        lru.AddOrRefresh("b", "2");
        lru.AddOrRefresh("a", "1");

        Assert.That(lru.TryGetLast(out string? last), Is.True);
        Assert.That(last, Is.EqualTo("2"));
    }

    [Test]
    public void Remove_FreesCapacityForNewEntries()
    {
        DoubleEndedLru<string, string> lru = new(2);
        lru.AddOrRefresh("a", "1");
        lru.AddOrRefresh("b", "2");

        Assert.That(lru.Remove("a"), Is.True);
        Assert.That(lru.Remove("a"), Is.False);
        Assert.That(lru.AddOrRefresh("c", "3"), Is.EqualTo(BucketAddResult.Added));
        Assert.That(lru.GetAll(), Is.EqualTo(new[] { "3", "2" }));
    }

    [Test]
    public void Clear_EmptiesAndAllowsReuse()
    {
        DoubleEndedLru<string, string> lru = new(2);
        lru.AddOrRefresh("a", "1");
        lru.AddOrRefresh("b", "2");

        lru.Clear();

        Assert.That(lru.Count, Is.Zero);
        Assert.That(lru.GetAll(), Is.Empty);
        Assert.That(lru.AddOrRefresh("c", "3"), Is.EqualTo(BucketAddResult.Added));
        Assert.That(lru.GetAll(), Is.EqualTo(new[] { "3" }));
    }
}
