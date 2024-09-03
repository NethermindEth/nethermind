// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using NUnit.Framework;
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
}
