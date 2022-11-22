// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
