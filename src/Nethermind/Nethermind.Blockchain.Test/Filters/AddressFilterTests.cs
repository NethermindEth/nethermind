// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters;

[TestFixture]
public class AddressFilterTests
{
    [Test]
    public void Accepts_a_specific_address()
    {
        AddressFilter filter = new AddressFilter(TestItem.AddressA);

        filter.Accepts(TestItem.AddressA).Should().BeTrue();
    }

    [Test]
    public void Accepts_a_specific_address_by_ref()
    {
        AddressFilter filter = new AddressFilter(TestItem.AddressA);

        AddressStructRef @ref = TestItem.AddressA.ToStructRef();
        filter.Accepts(ref @ref).Should().BeTrue();
    }

    [Test]
    public void Rejects_different_address()
    {
        AddressFilter filter = new AddressFilter(TestItem.AddressA);

        filter.Accepts(TestItem.AddressB).Should().BeFalse();
    }

    [Test]
    public void Rejects_different_address_by_ref()
    {
        AddressFilter filter = new AddressFilter(TestItem.AddressA);

        AddressStructRef addressBRef = TestItem.AddressB.ToStructRef();
        filter.Accepts(ref addressBRef).Should().BeFalse();
    }

    [Test]
    public void Accepts_any_address()
    {
        AddressFilter filter = AddressFilter.AnyAddress;

        filter.Accepts(TestItem.AddressA).Should().BeTrue();
        filter.Accepts(TestItem.AddressB).Should().BeTrue();
        filter.Accepts(TestItem.AddressC).Should().BeTrue();
    }

    [Test]
    public void Accepts_any_address_by_ref()
    {
        AddressFilter filter = AddressFilter.AnyAddress;

        AddressStructRef addressARef = TestItem.AddressA.ToStructRef();
        AddressStructRef addressBRef = TestItem.AddressB.ToStructRef();
        AddressStructRef addressCRef = TestItem.AddressC.ToStructRef();
        filter.Accepts(ref addressARef).Should().BeTrue();
        filter.Accepts(ref addressBRef).Should().BeTrue();
        filter.Accepts(ref addressCRef).Should().BeTrue();
    }

    [Test]
    public void Accepts_any_address_when_set_is_empty()
    {
        HashSet<Address> addresses = new();
        AddressFilter filter = new AddressFilter(addresses);

        filter.Accepts(TestItem.AddressA).Should().BeTrue();
        filter.Accepts(TestItem.AddressB).Should().BeTrue();
        filter.Accepts(TestItem.AddressC).Should().BeTrue();
    }

    [Test]
    public void Accepts_any_address_when_set_is_empty_by_ref()
    {
        HashSet<Address> addresses = new();
        AddressFilter filter = new AddressFilter(addresses);

        AddressStructRef addressARef = TestItem.AddressA.ToStructRef();
        AddressStructRef addressBRef = TestItem.AddressB.ToStructRef();
        AddressStructRef addressCRef = TestItem.AddressC.ToStructRef();
        filter.Accepts(ref addressARef).Should().BeTrue();
        filter.Accepts(ref addressBRef).Should().BeTrue();
        filter.Accepts(ref addressCRef).Should().BeTrue();
    }

    [Test]
    public void Accepts_only_addresses_in_a_set()
    {
        HashSet<Address> addresses = new()
        {
            TestItem.AddressA, TestItem.AddressC
        };
        AddressFilter filter = new AddressFilter(addresses);

        filter.Accepts(TestItem.AddressA).Should().BeTrue();
        filter.Accepts(TestItem.AddressB).Should().BeFalse();
        filter.Accepts(TestItem.AddressC).Should().BeTrue();
    }

    [Test]
    public void Accepts_only_addresses_in_a_set_by_ref()
    {
        HashSet<Address> addresses = new()
        {
            TestItem.AddressA, TestItem.AddressC
        };
        AddressFilter filter = new AddressFilter(addresses);

        AddressStructRef addressARef = TestItem.AddressA.ToStructRef();
        AddressStructRef addressBRef = TestItem.AddressB.ToStructRef();
        AddressStructRef addressCRef = TestItem.AddressC.ToStructRef();
        filter.Accepts(ref addressARef).Should().BeTrue();
        filter.Accepts(ref addressBRef).Should().BeFalse();
        filter.Accepts(ref addressCRef).Should().BeTrue();
    }

    [Test]
    public void Matches_bloom_using_specific_address()
    {
        AddressFilter filter = new AddressFilter(TestItem.AddressA);
        Core.Bloom bloom = BloomFromAddress(TestItem.AddressA);

        filter.Matches(bloom).Should().BeTrue();
    }

    [Test]
    public void Matches_bloom_using_specific_address_by_ref()
    {
        AddressFilter filter = new AddressFilter(TestItem.AddressA);
        BloomStructRef bloomRef = BloomFromAddress(TestItem.AddressA).ToStructRef();

        filter.Matches(ref bloomRef).Should().BeTrue();
    }

    [Test]
    public void Does_not_match_bloom_using_different_address()
    {
        AddressFilter filter = new AddressFilter(TestItem.AddressA);

        filter.Matches(BloomFromAddress(TestItem.AddressB)).Should().BeFalse();
    }

    [Test]
    public void Does_not_match_bloom_using_different_address_by_ref()
    {
        AddressFilter filter = new AddressFilter(TestItem.AddressA);
        BloomStructRef bloomRef = BloomFromAddress(TestItem.AddressB).ToStructRef();

        filter.Matches(ref bloomRef).Should().BeFalse();
    }

    [Test]
    public void Matches_bloom_using_any_address()
    {
        AddressFilter filter = AddressFilter.AnyAddress;

        filter.Matches(BloomFromAddress(TestItem.AddressA)).Should().BeTrue();
        filter.Matches(BloomFromAddress(TestItem.AddressB)).Should().BeTrue();
        filter.Matches(BloomFromAddress(TestItem.AddressC)).Should().BeTrue();
    }

    [Test]
    public void Matches_bloom_using_any_address_by_ref()
    {
        AddressFilter filter = AddressFilter.AnyAddress;

        BloomStructRef bloomARef = BloomFromAddress(TestItem.AddressA).ToStructRef();
        BloomStructRef bloomBRef = BloomFromAddress(TestItem.AddressB).ToStructRef();
        BloomStructRef bloomCRef = BloomFromAddress(TestItem.AddressC).ToStructRef();
        filter.Matches(ref bloomARef).Should().BeTrue();
        filter.Matches(ref bloomBRef).Should().BeTrue();
        filter.Matches(ref bloomCRef).Should().BeTrue();
    }

    [Test]
    public void Matches_any_bloom_when_set_is_empty()
    {
        HashSet<Address> addresses = new();
        AddressFilter filter = new AddressFilter(addresses);

        filter.Matches(BloomFromAddress(TestItem.AddressA)).Should().BeTrue();
        filter.Matches(BloomFromAddress(TestItem.AddressB)).Should().BeTrue();
        filter.Matches(BloomFromAddress(TestItem.AddressC)).Should().BeTrue();
    }

    [Test]
    public void Matches_any_bloom_when_set_is_empty_by_ref()
    {
        HashSet<Address> addresses = new();
        AddressFilter filter = new AddressFilter(addresses);

        BloomStructRef bloomARef = BloomFromAddress(TestItem.AddressA).ToStructRef();
        BloomStructRef bloomBRef = BloomFromAddress(TestItem.AddressB).ToStructRef();
        BloomStructRef bloomCRef = BloomFromAddress(TestItem.AddressC).ToStructRef();
        filter.Matches(ref bloomARef).Should().BeTrue();
        filter.Matches(ref bloomBRef).Should().BeTrue();
        filter.Matches(ref bloomCRef).Should().BeTrue();
    }

    [Test]
    public void Matches_any_bloom_when_set_is_forced_null()
    {
        AddressFilter filter = new AddressFilter(addresses: null!);

        filter.Matches(BloomFromAddress(TestItem.AddressA)).Should().BeTrue();
        filter.Matches(BloomFromAddress(TestItem.AddressB)).Should().BeTrue();
        filter.Matches(BloomFromAddress(TestItem.AddressC)).Should().BeTrue();
    }

    [Test]
    public void Matches_any_bloom_when_set_is_forced_null_by_ref()
    {
        AddressFilter filter = new AddressFilter(addresses: null!);

        BloomStructRef bloomARef = BloomFromAddress(TestItem.AddressA).ToStructRef();
        BloomStructRef bloomBRef = BloomFromAddress(TestItem.AddressB).ToStructRef();
        BloomStructRef bloomCRef = BloomFromAddress(TestItem.AddressC).ToStructRef();
        filter.Matches(ref bloomARef).Should().BeTrue();
        filter.Matches(ref bloomBRef).Should().BeTrue();
        filter.Matches(ref bloomCRef).Should().BeTrue();
    }

    [Test]
    public void Matches_any_bloom_using_addresses_set()
    {
        HashSet<Address> addresses = new()
        {
            TestItem.AddressA, TestItem.AddressC
        };
        AddressFilter filter = new AddressFilter(addresses);

        filter.Matches(BloomFromAddress(TestItem.AddressA)).Should().BeTrue();
        filter.Matches(BloomFromAddress(TestItem.AddressB)).Should().BeFalse();
        filter.Matches(BloomFromAddress(TestItem.AddressC)).Should().BeTrue();
    }

    [Test]
    public void Matches_any_bloom_using_addresses_set_by_ref()
    {
        HashSet<Address> addresses = new()
        {
            TestItem.AddressA, TestItem.AddressC
        };
        AddressFilter filter = new AddressFilter(addresses);

        BloomStructRef bloomARef = BloomFromAddress(TestItem.AddressA).ToStructRef();
        BloomStructRef bloomBRef = BloomFromAddress(TestItem.AddressB).ToStructRef();
        BloomStructRef bloomCRef = BloomFromAddress(TestItem.AddressC).ToStructRef();
        filter.Matches(ref bloomARef).Should().BeTrue();
        filter.Matches(ref bloomBRef).Should().BeFalse();
        filter.Matches(ref bloomCRef).Should().BeTrue();
    }

    private static Core.Bloom BloomFromAddress(Address address)
    {
        LogEntry entry = new LogEntry(address, new byte[] { }, new Keccak[] { });
        Core.Bloom bloom = new Core.Bloom(new[] { entry });

        return bloom;
    }
}
