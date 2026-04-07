// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using System.Linq;
using Nethermind.Int256;
using NUnit.Framework;

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
                BitVec = new BitArray(10),
            }).ToArray(),
            BitVec = new BitArray(10),
        };

        byte[] encoded = SszEncoding.Encode(test);
        SszEncoding.Merkleize(test, out UInt256 root);
        SszEncoding.Decode(encoded, out ComplexStruct decodedTest);

        Assert.That(decodedTest.VariableC.Fixed1, Is.EqualTo(test.VariableC.Fixed1));
        Assert.That(decodedTest.VariableC.Fixed2, Is.EqualTo(test.VariableC.Fixed2));
        SszEncoding.Merkleize(test, out UInt256 decodedRoot);
        Assert.That(root, Is.EqualTo(decodedRoot));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(100)]
    public void MerkleizeList_UnionType_MixesInCountNotLimit(int itemCount)
    {
        UnionTest3[] list = new UnionTest3[itemCount];

        for (int i = 0; i < itemCount; i++)
        {
            list[i] = new UnionTest3 { Selector = Test3Union.Test1, Test1 = 0 };
        }

        SszEncoding.MerkleizeList(list, 100, out UInt256 rootWithLimit100);
        SszEncoding.MerkleizeList(list, 200, out UInt256 rootWithLimit200);

        Assert.That(rootWithLimit100, Is.EqualTo(rootWithLimit200),
           "Same list with different limits must have same root (limit affects tree depth, not mix-in)");
    }

    [Test]
    public void Test_Bitvector_Roundtrip_Preserves_Length_And_Bits()
    {
        // Regression test: Generated Decode for bitvector fields called the bitlist overload
        // which strips a sentinel bit, corrupting the bit count.
        BitArray original = new BitArray(10);
        original[0] = true;
        original[3] = true;
        original[9] = true;

        BitvectorContainer test = new() { Value = 42, Bits = original };

        byte[] encoded = SszEncoding.Encode(test);
        SszEncoding.Decode(encoded, out BitvectorContainer decoded);

        Assert.That(decoded.Bits!.Length, Is.EqualTo(10), "Bitvector length must survive decode");
        Assert.That(decoded.Bits[0], Is.True);
        Assert.That(decoded.Bits[3], Is.True);
        Assert.That(decoded.Bits[9], Is.True);
        Assert.That(decoded.Bits[1], Is.False);
    }

    [Test]
    public void Test_Container_Decode_Rejects_Truncated_Input()
    {
        // Regression test: Generated Decode had no validation
        // truncated input produced garbage instead of throwing.
        byte[] tooShort = new byte[4];
        Assert.Throws<InvalidDataException>(() => SszEncoding.Decode(tooShort, out VariableC _));
    }

    [Test]
    public void Test_Container_Decode_Rejects_Invalid_Offset()
    {
        // First variable offset must equal staticLength (12 for VariableC).
        // Encode valid data then corrupt the offset.
        VariableC valid = new() { Fixed1 = 1, Fixed2 = [10, 20] };
        byte[] encoded = SszEncoding.Encode(valid);

        // Corrupt the offset at bytes 8-11 to point past the end
        encoded[8] = 0xFF;
        encoded[9] = 0xFF;
        encoded[10] = 0x00;
        encoded[11] = 0x00;

        Assert.Throws<InvalidDataException>(() => SszEncoding.Decode(encoded, out VariableC _));
    }
}
