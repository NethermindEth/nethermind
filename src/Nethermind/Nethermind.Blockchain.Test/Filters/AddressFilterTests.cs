// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Facade.Filters;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class AddressFilterTests
{
    [Test]
    public void Accepts_a_specific_address()
    {
        AddressFilter filter = new(TestItem.AddressA);

        Assert.That(filter.Accepts(TestItem.AddressA), Is.True);
    }

    [Test]
    public void Accepts_a_specific_address_by_ref()
    {
        AddressFilter filter = new(TestItem.AddressA);

        AddressStructRef @ref = TestItem.AddressA.ToStructRef();
        Assert.That(filter.Accepts(ref @ref), Is.True);
    }

    [Test]
    public void Rejects_different_address()
    {
        AddressFilter filter = new(TestItem.AddressA);

        Assert.That(filter.Accepts(TestItem.AddressB), Is.False);
    }

    [Test]
    public void Rejects_different_address_by_ref()
    {
        AddressFilter filter = new(TestItem.AddressA);

        AddressStructRef addressBRef = TestItem.AddressB.ToStructRef();
        Assert.That(filter.Accepts(ref addressBRef), Is.False);
    }

    [Test]
    public void Accepts_any_address()
    {
        AddressFilter filter = AddressFilter.AnyAddress;

        Assert.That(filter.Accepts(TestItem.AddressA), Is.True);
        Assert.That(filter.Accepts(TestItem.AddressB), Is.True);
        Assert.That(filter.Accepts(TestItem.AddressC), Is.True);
    }

    [Test]
    public void Accepts_any_address_by_ref()
    {
        AddressFilter filter = AddressFilter.AnyAddress;

        AddressStructRef addressARef = TestItem.AddressA.ToStructRef();
        AddressStructRef addressBRef = TestItem.AddressB.ToStructRef();
        AddressStructRef addressCRef = TestItem.AddressC.ToStructRef();
        Assert.That(filter.Accepts(ref addressARef), Is.True);
        Assert.That(filter.Accepts(ref addressBRef), Is.True);
        Assert.That(filter.Accepts(ref addressCRef), Is.True);
    }

    [Test]
    public void Accepts_any_address_when_set_is_empty()
    {
        HashSet<AddressAsKey> addresses = [];
        AddressFilter filter = new(addresses);

        Assert.That(filter.Accepts(TestItem.AddressA), Is.True);
        Assert.That(filter.Accepts(TestItem.AddressB), Is.True);
        Assert.That(filter.Accepts(TestItem.AddressC), Is.True);
    }

    [Test]
    public void Accepts_any_address_when_set_is_empty_by_ref()
    {
        HashSet<AddressAsKey> addresses = [];
        AddressFilter filter = new(addresses);

        AddressStructRef addressARef = TestItem.AddressA.ToStructRef();
        AddressStructRef addressBRef = TestItem.AddressB.ToStructRef();
        AddressStructRef addressCRef = TestItem.AddressC.ToStructRef();
        Assert.That(filter.Accepts(ref addressARef), Is.True);
        Assert.That(filter.Accepts(ref addressBRef), Is.True);
        Assert.That(filter.Accepts(ref addressCRef), Is.True);
    }

    [Test]
    public void Accepts_only_addresses_in_a_set()
    {
        HashSet<AddressAsKey> addresses = [TestItem.AddressA, TestItem.AddressC];
        AddressFilter filter = new(addresses);

        Assert.That(filter.Accepts(TestItem.AddressA), Is.True);
        Assert.That(filter.Accepts(TestItem.AddressB), Is.False);
        Assert.That(filter.Accepts(TestItem.AddressC), Is.True);
    }

    [Test]
    public void Accepts_only_addresses_in_a_set_by_ref()
    {
        HashSet<AddressAsKey> addresses = [TestItem.AddressA, TestItem.AddressC];
        AddressFilter filter = new(addresses);

        AddressStructRef addressARef = TestItem.AddressA.ToStructRef();
        AddressStructRef addressBRef = TestItem.AddressB.ToStructRef();
        AddressStructRef addressCRef = TestItem.AddressC.ToStructRef();
        Assert.That(filter.Accepts(ref addressARef), Is.True);
        Assert.That(filter.Accepts(ref addressBRef), Is.False);
        Assert.That(filter.Accepts(ref addressCRef), Is.True);
    }

    [Test]
    public void Matches_bloom_using_specific_address()
    {
        AddressFilter filter = new(TestItem.AddressA);
        Core.Bloom bloom = BloomFromAddress(TestItem.AddressA);

        Assert.That(filter.Matches(bloom), Is.True);
    }

    [Test]
    public void Matches_bloom_using_specific_address_by_ref()
    {
        AddressFilter filter = new(TestItem.AddressA);
        BloomStructRef bloomRef = BloomFromAddress(TestItem.AddressA).ToStructRef();

        Assert.That(filter.Matches(ref bloomRef), Is.True);
    }

    [Test]
    public void Does_not_match_bloom_using_different_address()
    {
        AddressFilter filter = new(TestItem.AddressA);

        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressB)), Is.False);
    }

    [Test]
    public void Does_not_match_bloom_using_different_address_by_ref()
    {
        AddressFilter filter = new(TestItem.AddressA);
        BloomStructRef bloomRef = BloomFromAddress(TestItem.AddressB).ToStructRef();

        Assert.That(filter.Matches(ref bloomRef), Is.False);
    }

    [Test]
    public void Matches_bloom_using_any_address()
    {
        AddressFilter filter = AddressFilter.AnyAddress;

        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressA)), Is.True);
        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressB)), Is.True);
        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressC)), Is.True);
    }

    [Test]
    public void Matches_bloom_using_any_address_by_ref()
    {
        AddressFilter filter = AddressFilter.AnyAddress;

        BloomStructRef bloomARef = BloomFromAddress(TestItem.AddressA).ToStructRef();
        BloomStructRef bloomBRef = BloomFromAddress(TestItem.AddressB).ToStructRef();
        BloomStructRef bloomCRef = BloomFromAddress(TestItem.AddressC).ToStructRef();
        Assert.That(filter.Matches(ref bloomARef), Is.True);
        Assert.That(filter.Matches(ref bloomBRef), Is.True);
        Assert.That(filter.Matches(ref bloomCRef), Is.True);
    }

    [Test]
    public void Matches_any_bloom_when_set_is_empty()
    {
        HashSet<AddressAsKey> addresses = [];
        AddressFilter filter = new(addresses);

        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressA)), Is.True);
        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressB)), Is.True);
        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressC)), Is.True);
    }

    [Test]
    public void Matches_any_bloom_when_set_is_empty_by_ref()
    {
        HashSet<AddressAsKey> addresses = [];
        AddressFilter filter = new(addresses);

        BloomStructRef bloomARef = BloomFromAddress(TestItem.AddressA).ToStructRef();
        BloomStructRef bloomBRef = BloomFromAddress(TestItem.AddressB).ToStructRef();
        BloomStructRef bloomCRef = BloomFromAddress(TestItem.AddressC).ToStructRef();
        Assert.That(filter.Matches(ref bloomARef), Is.True);
        Assert.That(filter.Matches(ref bloomBRef), Is.True);
        Assert.That(filter.Matches(ref bloomCRef), Is.True);
    }

    [Test]
    public void Matches_any_bloom_when_set_is_forced_null()
    {
        AddressFilter filter = new([]);

        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressA)), Is.True);
        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressB)), Is.True);
        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressC)), Is.True);
    }

    [Test]
    public void Matches_any_bloom_when_set_is_forced_null_by_ref()
    {
        AddressFilter filter = new([]);

        BloomStructRef bloomARef = BloomFromAddress(TestItem.AddressA).ToStructRef();
        BloomStructRef bloomBRef = BloomFromAddress(TestItem.AddressB).ToStructRef();
        BloomStructRef bloomCRef = BloomFromAddress(TestItem.AddressC).ToStructRef();
        Assert.That(filter.Matches(ref bloomARef), Is.True);
        Assert.That(filter.Matches(ref bloomBRef), Is.True);
        Assert.That(filter.Matches(ref bloomCRef), Is.True);
    }

    [Test]
    public void Matches_any_bloom_using_addresses_set()
    {
        HashSet<AddressAsKey> addresses = [TestItem.AddressA, TestItem.AddressC];
        AddressFilter filter = new(addresses);

        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressA)), Is.True);
        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressB)), Is.False);
        Assert.That(filter.Matches(BloomFromAddress(TestItem.AddressC)), Is.True);
    }

    [Test]
    public void Matches_any_bloom_using_addresses_set_by_ref()
    {
        HashSet<AddressAsKey> addresses = [TestItem.AddressA, TestItem.AddressC];
        AddressFilter filter = new(addresses);

        BloomStructRef bloomARef = BloomFromAddress(TestItem.AddressA).ToStructRef();
        BloomStructRef bloomBRef = BloomFromAddress(TestItem.AddressB).ToStructRef();
        BloomStructRef bloomCRef = BloomFromAddress(TestItem.AddressC).ToStructRef();
        Assert.That(filter.Matches(ref bloomARef), Is.True);
        Assert.That(filter.Matches(ref bloomBRef), Is.False);
        Assert.That(filter.Matches(ref bloomCRef), Is.True);
    }

    private static Core.Bloom BloomFromAddress(Address address)
    {
        LogEntry entry = new(address, [], []);
        Core.Bloom bloom = new([entry]);

        return bloom;
    }
}
