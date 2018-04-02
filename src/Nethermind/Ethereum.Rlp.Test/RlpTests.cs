/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using NUnit.Framework;

namespace Ethereum.Rlp.Test
{
    [TestFixture]
    public class RlpTests
    {
        private class RlpTestJson
        {
            public object In { get; set; }
            public string Out { get; set; }
        }

        [DebuggerDisplay("{Name}")]
        public class RlpTest
        {
            public RlpTest(string name, object input, string output)
            {
                Name = name;
                Input = input;
                Output = output;
            }

            public string Name { get; }
            public object Input { get; }
            public string Output { get; }

            public override string ToString()
            {
                return $"{Name} exp: {Output}";
            }
        }

        private static IEnumerable<RlpTest> LoadValidTests()
        {
            return LoadTests("rlptest.json");
        }

        private static IEnumerable<RlpTest> LoadRandomTests()
        {
            return LoadTests("example.json");
        }

        private static IEnumerable<RlpTest> LoadInvalidTests()
        {
            return LoadTests("invalidRLPTest.json");
        }

        private static IEnumerable<RlpTest> LoadTests(string testFileName)
        {
            return TestLoader.LoadFromFile<Dictionary<string, RlpTestJson>, RlpTest>(
                testFileName,
                c => c.Select(p => new RlpTest(p.Key, p.Value.In, p.Value.Out)));
        }

        [TestCaseSource(nameof(LoadValidTests))]
        public void Test(RlpTest test)
        {
            object input = TestLoader.PrepareInput(test.Input);

            Nethermind.Core.Encoding.Rlp serialized = Nethermind.Core.Encoding.Rlp.Encode(input);
            string serializedHex = serialized.ToString(false);

            object deserialized = Nethermind.Core.Encoding.Rlp.Decode(serialized);
            Nethermind.Core.Encoding.Rlp serializedAgain = Nethermind.Core.Encoding.Rlp.Encode(deserialized);
            string serializedAgainHex = serializedAgain.ToString(false);

            Assert.AreEqual(test.Output, serializedHex);
            Assert.AreEqual(serializedHex, serializedAgainHex);
        }

        [TestCaseSource(nameof(LoadInvalidTests))]
        public void TestInvalid(RlpTest test)
        {
            Nethermind.Core.Encoding.Rlp invalidBytes = new Nethermind.Core.Encoding.Rlp(Hex.ToBytes(test.Output));
            Assert.Throws<RlpException>(
                () => Nethermind.Core.Encoding.Rlp.Decode(invalidBytes));
        }

        [TestCaseSource(nameof(LoadRandomTests))]
        public void TestRandom(RlpTest test)
        {
            Nethermind.Core.Encoding.Rlp validBytes = new Nethermind.Core.Encoding.Rlp(Hex.ToBytes(test.Output));
            Nethermind.Core.Encoding.Rlp.Decode(validBytes);
        }

        [Test]
        public void TestEmpty()
        {
            Assert.AreEqual(Nethermind.Core.Encoding.Rlp.OfEmptyByteArray, Nethermind.Core.Encoding.Rlp.Encode(new byte[0]));
            Assert.AreEqual(Nethermind.Core.Encoding.Rlp.OfEmptySequence, Nethermind.Core.Encoding.Rlp.Encode(new object[] { }));
        }

        [Test]
        public void TestCast()
        {
            byte[] expected = new byte[] {1};
            Assert.AreEqual(expected, Nethermind.Core.Encoding.Rlp.Encode((byte)1).Bytes, "byte");
            Assert.AreEqual(expected, Nethermind.Core.Encoding.Rlp.Encode((short)1).Bytes, "short");
            Assert.AreEqual(expected, Nethermind.Core.Encoding.Rlp.Encode((ushort)1).Bytes, "ushort");
            Assert.AreEqual(expected, Nethermind.Core.Encoding.Rlp.Encode(1).Bytes, "int");
            Assert.AreEqual(expected, Nethermind.Core.Encoding.Rlp.Encode(1U).Bytes, "uint bytes");
            Assert.AreEqual(expected, Nethermind.Core.Encoding.Rlp.Encode(1L).Bytes, "long bytes");

            byte[] expectedUlong = new byte[] {136, 0, 0, 0, 0, 0, 0, 0, 1};
            Assert.AreEqual(expectedUlong, Nethermind.Core.Encoding.Rlp.Encode(1UL).Bytes, "ulong bytes");
        }

        [Test]
        public void TestNonce()
        {
            byte[] expected = {136, 0, 0, 0, 0, 0, 0, 0, 42};
            Assert.AreEqual(expected, Nethermind.Core.Encoding.Rlp.Encode(42UL).Bytes);
        }
        
        [Ignore("placeholder for various rlp tests")]
        [Test]
        public void VariousTests()
        {
            List<object> objects = new List<object>();
            objects.Add(0);

            byte[] result = Nethermind.Core.Encoding.Rlp.Encode(objects).Bytes;
            
            
            List<byte[]> bytes = new List<byte[]>();
            bytes.Add(Nethermind.Core.Encoding.Rlp.Encode(0).Bytes);
            
            byte[] resultBytes = Nethermind.Core.Encoding.Rlp.Encode(bytes).Bytes;
            
            List<object> bytesRlp = new List<object>();
            bytesRlp.Add(Nethermind.Core.Encoding.Rlp.Encode(0));
            
            byte[] resultRlp = Nethermind.Core.Encoding.Rlp.Encode(bytesRlp).Bytes;
            
            Assert.AreEqual(resultRlp, result);
            Assert.AreEqual(result, resultBytes);
        }
    }
}