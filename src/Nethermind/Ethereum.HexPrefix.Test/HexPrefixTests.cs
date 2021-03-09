/*
 * Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Ethereum.HexPrefix.Test
{
    [Parallelizable(ParallelScope.All)]
    public class HexPrefixTests
    {
        // ReSharper disable once MemberCanBePrivate.Global
        // used as a test case source, hasbe public
        public static IEnumerable<HexPrefixTest> LoadTests()
        {
            return TestLoader.LoadFromFile<Dictionary<string, HexPrefixTestJson>, HexPrefixTest>(
                "hexencodetest.json",
                c => c.Select(p => new HexPrefixTest(p.Key, p.Value.Seq, p.Value.Term, p.Value.Out)));
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(HexPrefixTest test)
        {
            Nethermind.Trie.HexPrefix result =
                new Nethermind.Trie.HexPrefix(test.IsTerm, test.Sequence);
            byte[] bytes = result.ToBytes();
            string resultHex = bytes.ToHexString(false);
            Assert.AreEqual(test.Output, resultHex);

            Nethermind.Trie.HexPrefix check = Nethermind.Trie.HexPrefix.FromBytes(bytes);
            byte[] checkBytes = check.ToBytes();
            string checkHex = checkBytes.ToHexString(false);
            Assert.AreEqual(test.Output, checkHex);
        }

        private class HexPrefixTestJson
        {
            public byte[] Seq { get; set; }
            public bool Term { get; set; }
            public string Out { get; set; }
        }

        public class HexPrefixTest
        {
            public HexPrefixTest(string name, byte[] sequence, bool isTerm, string output)
            {
                Name = name;
                Sequence = sequence;
                IsTerm = isTerm;
                Output = output;
            }

            public string Name { get; }
            public byte[] Sequence { get; }
            public bool IsTerm { get; }
            public string Output { get; }

            public override string ToString()
            {
                return $"{Name}, exp: {Output}";
            }
        }
    }
}
