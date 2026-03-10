// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;
using System.Collections;
using System.IO;
using System.Linq;

namespace Nethermind.Serialization.SszGenerator.Test;

public class EncodingTest
{
    [Test]
    public void Test_ComplexStructure_EncodingRoundTrip()
    {
        ComplexStruct test = new()
        {
            VariableC = new VariableC { Fixed1 = 2, Fixed2 = [1, 2, 3, 4] },
            Test2 = Enumerable.Range(0, 10).Select(i => (ulong)i).ToArray(),
            FixedCVec = Enumerable.Range(0, 10).Select(i => new FixedC()).ToArray(),
            VariableCVec = Enumerable.Range(0, 10).Select(i => new VariableC()).ToArray(),
            Test2UnionVec = Enumerable.Range(0, 10).Select(i => new UnionTest3()
            {
                VariableC = new VariableC { Fixed1 = 2, Fixed2 = [1, 2, 3, 4] },
                Test2 = Enumerable.Range(0, 10).Select(i => (ulong)i).ToArray(),
                FixedCVec = Enumerable.Range(0, 10).Select(i => new FixedC()).ToArray(),
                VariableCVec = Enumerable.Range(0, 10).Select(i => new VariableC()).ToArray(),
                BitVec = new System.Collections.BitArray(10),
            }).ToArray(),
            BitVec = new System.Collections.BitArray(10),
        };

        var encoded = SszEncoding.Encode(test);
        SszEncoding.Merkleize(test, out UInt256 root);
        SszEncoding.Decode(encoded, out ComplexStruct decodedTest);

        Assert.That(decodedTest.VariableC.Fixed1, Is.EqualTo(test.VariableC.Fixed1));
        Assert.That(decodedTest.VariableC.Fixed2, Is.EqualTo(test.VariableC.Fixed2));
        SszEncoding.Merkleize(test, out UInt256 decodedRoot);
        Assert.That(root, Is.EqualTo(decodedRoot));
    }

    [Test]
    public void Decode_empty_variable_lists_as_empty_collections()
    {
        byte[] encoded = [8, 0, 0, 0, 8, 0, 0, 0];

        SszEncoding.Decode(encoded, out DoubleListContainer decoded);

        Assert.That(decoded.First, Is.Not.Null);
        Assert.That(decoded.First, Is.Empty);
        Assert.That(decoded.Second, Is.Not.Null);
        Assert.That(decoded.Second, Is.Empty);
    }

    [Test]
    public void Decode_bitvector_preserves_declared_length()
    {
        BitVectorContainer container = new() { Bits = new BitArray(10) };

        byte[] encoded = SszEncoding.Encode(container);
        SszEncoding.Decode(encoded, out BitVectorContainer decoded);

        Assert.That(decoded.Bits, Is.Not.Null);
        Assert.That(decoded.Bits!.Length, Is.EqualTo(10));
        Assert.That(decoded.Bits.Cast<bool>(), Is.EqualTo(container.Bits!.Cast<bool>()));
    }

    [Test]
    public void Merkleize_basic_list_mixes_in_the_actual_length()
    {
        SingleListContainer container = new() { Items = [1UL, 2UL] };

        SszEncoding.Merkleize(container, out UInt256 actual);

        ulong[] items = [1UL, 2UL];
        Merkle.Merkleize(out UInt256 expected, items, 4);
        Merkle.MixIn(ref expected, items.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Merkleize_union_matches_the_selected_value_root()
    {
        Test2 container = new() { Selector = Test2Union.Type1, Type1 = 123L };

        SszEncoding.Merkleize(container, out UInt256 actual);

        Merkle.Merkleize(out UInt256 expected, container.Type1);
        Merkle.MixIn(ref expected, (byte)container.Selector);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Decode_rejects_unknown_union_selector()
    {
        Assert.That(
            () => SszEncoding.Decode([99], out Test2 _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Decode_rejects_offsets_that_point_into_the_fixed_section()
    {
        byte[] encoded = [4, 0, 0, 0, 8, 0, 0, 0];

        Assert.That(
            () => SszEncoding.Decode(encoded, out DoubleListContainer _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Encode_rejects_vectors_with_the_wrong_length()
    {
        FixedVectorContainer container = new() { Items = [new FixedC()] };

        Assert.That(
            () => SszEncoding.Encode(container),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Encode_rejects_lists_above_the_declared_limit()
    {
        SingleListContainer container = new() { Items = [1UL, 2UL, 3UL, 4UL, 5UL] };

        Assert.That(
            () => SszEncoding.Encode(container),
            Throws.InstanceOf<InvalidDataException>());
    }
}
