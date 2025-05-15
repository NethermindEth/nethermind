// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using Ethereum.Test.Base;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
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
                c => c.Select(p => new HexPrefixTest(p.Key, p.Value.Seq.Select(x => (byte)x).ToArray(), p.Value.Term, p.Value.Out)));
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(HexPrefixTest test)
        {
            NibblePath path = NibblePath.FromNibbles(test.Sequence);

            // encode
            byte[] bytes = new byte[path.ByteLength];
            path.EncodeTo(bytes, test.IsTerm);
            string resultHex = bytes.ToHexString(false);
            Assert.That(resultHex, Is.EqualTo(test.Output));

            // decode
            (NibblePath key, bool isLeaf) = NibblePath.FromRlpBytes(bytes);
            byte[] checkBytes = new byte[key.ByteLength];
            key.EncodeTo(checkBytes, isLeaf);
            string checkHex = checkBytes.ToHexString(false);
            Assert.That(checkHex, Is.EqualTo(test.Output));
        }

        private class HexPrefixTestJson
        {
            public int[] Seq { get; set; }
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
