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
using JetBrains.Annotations;
using Nevermind.Core;
using NUnit.Framework;

namespace Ethereum.Rlp.Test
{
    [TestFixture]
    public class RlpTests
    {
        [UsedImplicitly]
        private class RlpTestJson
        {
            public object In { get; [UsedImplicitly] set; }
            public string Out { get; [UsedImplicitly] set; }
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

            Nevermind.Core.Encoding.Rlp serialized = Nevermind.Core.Encoding.Rlp.Encode(input);
            string serializedHex = serialized.ToString(false);

            object deserialized = Nevermind.Core.Encoding.Rlp.Decode(serialized);
            Nevermind.Core.Encoding.Rlp serializedAgain = Nevermind.Core.Encoding.Rlp.Encode(deserialized);
            string serializedAgainHex = serializedAgain.ToString(false);

            Assert.AreEqual(test.Output, serializedHex);
            Assert.AreEqual(serializedHex, serializedAgainHex);
        }

        [TestCaseSource(nameof(LoadInvalidTests))]
        public void TestInvalid(RlpTest test)
        {
            Nevermind.Core.Encoding.Rlp invalidBytes = new Nevermind.Core.Encoding.Rlp(Hex.ToBytes(test.Output));
            Assert.Throws<InvalidOperationException>(
                () => Nevermind.Core.Encoding.Rlp.Decode(invalidBytes));
        }

        [TestCaseSource(nameof(LoadRandomTests))]
        public void TestRandom(RlpTest test)
        {
            Nevermind.Core.Encoding.Rlp validBytes = new Nevermind.Core.Encoding.Rlp(Hex.ToBytes(test.Output));
            Nevermind.Core.Encoding.Rlp.Decode(validBytes);
        }

        [Test]
        public void TestEmpty()
        {
            Assert.AreEqual(Nevermind.Core.Encoding.Rlp.OfEmptyByteArray, Nevermind.Core.Encoding.Rlp.Encode(new byte[0]));
            Assert.AreEqual(Nevermind.Core.Encoding.Rlp.OfEmptySequence, Nevermind.Core.Encoding.Rlp.Encode(new object[] { }));
        }

        [Test]
        public void TestCast()
        {
            byte[] expected = new byte[] { 1 };
            Assert.AreEqual(expected, Nevermind.Core.Encoding.Rlp.Encode((byte)1).Bytes);
            Assert.AreEqual(expected, Nevermind.Core.Encoding.Rlp.Encode((short)1).Bytes);
            Assert.AreEqual(expected, Nevermind.Core.Encoding.Rlp.Encode((ushort)1).Bytes);
            Assert.AreEqual(expected, Nevermind.Core.Encoding.Rlp.Encode(1).Bytes);
            Assert.AreEqual(expected, Nevermind.Core.Encoding.Rlp.Encode(1U).Bytes);
            Assert.AreEqual(expected, Nevermind.Core.Encoding.Rlp.Encode(1L).Bytes);
            Assert.AreEqual(expected, Nevermind.Core.Encoding.Rlp.Encode(1UL).Bytes);
        }

        [Test]
        public void TestNonce()
        {
            byte[] expected = { 136, 0, 0, 0, 0, 0, 0, 0, 42 };
            Assert.AreEqual(expected, Nevermind.Core.Encoding.Rlp.Encode(42UL).Bytes);
        }
    }
}