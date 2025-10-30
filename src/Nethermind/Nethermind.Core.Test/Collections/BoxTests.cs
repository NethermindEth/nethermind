// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class BoxTests
{
    [Test]
    public void Box_can_wrap_null()
    {
        Box<Address> box = new(null);
        box.Value.Should().BeNull();
    }

    [Test]
    public void Box_can_wrap_value()
    {
        Address address = TestItem.AddressA;
        Box<Address> box = new(address);
        box.Value.Should().Be(address);
    }

    [Test]
    public void Box_implicit_conversion_from_value()
    {
        Address address = TestItem.AddressA;
        Box<Address> box = address;
        box.Value.Should().Be(address);
    }

    [Test]
    public void Box_implicit_conversion_to_value()
    {
        Address address = TestItem.AddressA;
        Box<Address> box = new(address);
        Address? result = box;
        result.Should().Be(address);
    }

    [Test]
    public void Box_equality_with_same_value()
    {
        Address address = TestItem.AddressA;
        Box<Address> box1 = address;
        Box<Address> box2 = address;
        box1.Equals(box2).Should().BeTrue();
    }

    [Test]
    public void Box_equality_with_equal_value()
    {
        Address address1 = new("0x0000000000000000000000000000000000000001");
        Address address2 = new("0x0000000000000000000000000000000000000001");
        Box<Address> box1 = address1;
        Box<Address> box2 = address2;
        box1.Equals(box2).Should().BeTrue();
    }

    [Test]
    public void Box_inequality_with_different_values()
    {
        Box<Address> box1 = TestItem.AddressA;
        Box<Address> box2 = TestItem.AddressB;
        box1.Equals(box2).Should().BeFalse();
    }

    [Test]
    public void Box_equality_with_both_null()
    {
        Box<Address> box1 = new(null);
        Box<Address> box2 = new(null);
        box1.Equals(box2).Should().BeTrue();
    }

    [Test]
    public void Box_inequality_one_null_one_value()
    {
        Box<Address> box1 = new(null);
        Box<Address> box2 = TestItem.AddressA;
        box1.Equals(box2).Should().BeFalse();
        box2.Equals(box1).Should().BeFalse();
    }

    [Test]
    public void Box_equals_object_with_same_value()
    {
        Box<Address> box1 = TestItem.AddressA;
        object box2 = new Box<Address>(TestItem.AddressA);
        box1.Equals(box2).Should().BeTrue();
    }

    [Test]
    public void Box_equals_object_with_different_type()
    {
        Box<Address> box = TestItem.AddressA;
        object other = "not a box";
        box.Equals(other).Should().BeFalse();
    }

    [Test]
    public void Box_hashcode_consistency()
    {
        Address address = TestItem.AddressA;
        Box<Address> box = address;
        box.GetHashCode().Should().Be(address.GetHashCode());
    }

    [Test]
    public void Box_hashcode_null_is_zero()
    {
        Box<Address> box = new(null);
        box.GetHashCode().Should().Be(0);
    }

    [Test]
    public void Box_can_be_used_as_dictionary_key()
    {
        Dictionary<Box<Address>, string> dictionary = new()
        {
            { TestItem.AddressA, "A" },
            { TestItem.AddressB, "B" },
            { TestItem.AddressC, "C" }
        };

        dictionary.ContainsKey(TestItem.AddressA).Should().BeTrue();
        dictionary[TestItem.AddressA].Should().Be("A");
        dictionary[TestItem.AddressB].Should().Be("B");
        dictionary[TestItem.AddressC].Should().Be("C");
    }

    [Test]
    public void Box_toString_returns_value_string()
    {
        Address address = TestItem.AddressA;
        Box<Address> box = address;
        box.ToString().Should().Be(address.ToString());
    }

    [Test]
    public void Box_toString_null_returns_placeholder()
    {
        Box<Address> box = new(null);
        box.ToString().Should().Be("<null>");
    }

    [Test]
    public void ComparableBox_can_wrap_null()
    {
        ComparableBox<Hash256> box = new(null);
        box.Value.Should().BeNull();
    }

    [Test]
    public void ComparableBox_can_wrap_value()
    {
        Hash256 hash = TestItem.KeccakA;
        ComparableBox<Hash256> box = new(hash);
        box.Value.Should().Be(hash);
    }

    [Test]
    public void ComparableBox_implicit_conversion_from_value()
    {
        Hash256 hash = TestItem.KeccakA;
        ComparableBox<Hash256> box = hash;
        box.Value.Should().Be(hash);
    }

    [Test]
    public void ComparableBox_implicit_conversion_to_value()
    {
        Hash256 hash = TestItem.KeccakA;
        ComparableBox<Hash256> box = new(hash);
        Hash256? result = box;
        result.Should().Be(hash);
    }

    [Test]
    public void ComparableBox_equality_with_same_value()
    {
        Hash256 hash = TestItem.KeccakA;
        ComparableBox<Hash256> box1 = hash;
        ComparableBox<Hash256> box2 = hash;
        box1.Equals(box2).Should().BeTrue();
    }

    [Test]
    public void ComparableBox_compare_same_values()
    {
        Hash256 hash = TestItem.KeccakA;
        ComparableBox<Hash256> box1 = hash;
        ComparableBox<Hash256> box2 = hash;
        box1.CompareTo(box2).Should().Be(0);
    }

    [Test]
    public void ComparableBox_compare_different_values()
    {
        ComparableBox<Hash256> box1 = TestItem.KeccakA;
        ComparableBox<Hash256> box2 = TestItem.KeccakB;
        int result = box1.CompareTo(box2);
        result.Should().Be(TestItem.KeccakA.CompareTo(TestItem.KeccakB));
    }

    [Test]
    public void ComparableBox_compare_null_with_value()
    {
        ComparableBox<Hash256> box1 = new(null);
        ComparableBox<Hash256> box2 = TestItem.KeccakA;
        box1.CompareTo(box2).Should().Be(-1);
        box2.CompareTo(box1).Should().Be(1);
    }

    [Test]
    public void ComparableBox_compare_both_null()
    {
        ComparableBox<Hash256> box1 = new(null);
        ComparableBox<Hash256> box2 = new(null);
        box1.CompareTo(box2).Should().Be(0);
    }

    [Test]
    public void ComparableBox_can_be_used_as_dictionary_key()
    {
        Dictionary<ComparableBox<Hash256>, string> dictionary = new()
        {
            { TestItem.KeccakA, "A" },
            { TestItem.KeccakB, "B" },
            { TestItem.KeccakC, "C" }
        };

        dictionary.ContainsKey(TestItem.KeccakA).Should().BeTrue();
        dictionary[TestItem.KeccakA].Should().Be("A");
        dictionary[TestItem.KeccakB].Should().Be("B");
        dictionary[TestItem.KeccakC].Should().Be("C");
    }

    [Test]
    public void ComparableBox_hashcode_consistency()
    {
        Hash256 hash = TestItem.KeccakA;
        ComparableBox<Hash256> box = hash;
        box.GetHashCode().Should().Be(hash.GetHashCode());
    }

    [Test]
    public void ComparableBox_hashcode_null_is_zero()
    {
        ComparableBox<Hash256> box = new(null);
        box.GetHashCode().Should().Be(0);
    }
}
