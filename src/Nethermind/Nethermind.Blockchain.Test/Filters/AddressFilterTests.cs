// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters;

[TestFixture]
public class AddressFilterTests
{
    private static readonly Address _address1 = Address.FromNumber(UInt256.Parse("1"));
    private static readonly Address _address2 = Address.FromNumber(UInt256.Parse("2"));
    private static readonly Address _address3 = Address.FromNumber(UInt256.Parse("3"));

    [Test]
    public void Accepts_a_specific_address()
    {
        AddressFilter filter = new AddressFilter(_address1);

        filter.Accepts(_address1).Should().BeTrue();
    }

    [Test]
    public void Rejects_different_address()
    {
        AddressFilter filter = new AddressFilter(_address1);

        filter.Accepts(_address2).Should().BeFalse();
    }

    [Test]
    public void Accepts_any_address_when_set_is_null()
    {
        AddressFilter filter = new AddressFilter(addresses: null!);

        filter.Accepts(_address1).Should().BeTrue();
        filter.Accepts(_address2).Should().BeTrue();
        filter.Accepts(_address3).Should().BeTrue();
    }

    [Test]
    public void Accepts_any_address_when_set_is_empty()
    {
        HashSet<Address> addresses = new();
        AddressFilter filter = new AddressFilter(addresses);

        filter.Accepts(_address1).Should().BeTrue();
        filter.Accepts(_address2).Should().BeTrue();
        filter.Accepts(_address3).Should().BeTrue();
    }

    [Test]
    public void Accepts_only_addresses_in_a_set()
    {
        HashSet<Address> addresses = new()
        {
            _address1, _address3
        };
        AddressFilter filter = new AddressFilter(addresses);

        filter.Accepts(_address1).Should().BeTrue();
        filter.Accepts(_address2).Should().BeFalse();
        filter.Accepts(_address3).Should().BeTrue();
    }
}
