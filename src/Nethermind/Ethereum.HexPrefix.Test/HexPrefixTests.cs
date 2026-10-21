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
        // used as a test case source, has to be public
        public static IEnumerable<HexPrefixTest> LoadTests() =>
            TestLoader.LoadFromFile<Dictionary<string, HexPrefixTestJson>, HexPrefixTest>(
                "hexencodetest.json",
                c => c.Select(p => new HexPrefixTest(p.Key, p.Value.Seq.Select(x => (byte)x).ToArray(), p.Value.Term, p.Value.Out)));

        [TestCaseSource(nameof(LoadTests))]
        public void Test(HexPrefixTest test)
        {
            byte[] bytes = Nethermind.Trie.HexPrefix.ToBytes(test.Sequence, test.IsTerm);
            string resultHex = bytes.ToHexString(false);
            Assert.That(resultHex, Is.EqualTo(test.Output));

            (byte[] key, bool isLeaf) = Nethermind.Trie.HexPrefix.FromBytes(bytes);
            Assert.That(isLeaf, Is.EqualTo(test.IsTerm));
            byte[] checkBytes = Nethermind.Trie.HexPrefix.ToBytes(key, isLeaf);
            string checkHex = checkBytes.ToHexString(false);
            Assert.That(checkHex, Is.EqualTo(test.Output));
        }

        private class HexPrefixTestJson
        {
            public int[] Seq { get; set; }
            public bool Term { get; set; }
            public string Out { get; set; }
        }

        public class HexPrefixTest(string name, byte[] sequence, bool isTerm, string output)
        {
            public string Name { get; } = name;
            public byte[] Sequence { get; } = sequence;
            public bool IsTerm { get; } = isTerm;
            public string Output { get; } = output;

            public override string ToString() => $"{Name}, exp: {Output}";
        }
    }
}
