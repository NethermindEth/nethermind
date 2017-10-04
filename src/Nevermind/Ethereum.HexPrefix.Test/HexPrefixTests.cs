using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using JetBrains.Annotations;
using Nevermind.Core.Encoding;
using NUnit.Framework;

namespace Ethereum.HexPrefix.Test
{
    public class HexPrefixTests
    {
        public static IEnumerable<HexPrefixTest> LoadTests()
        {
            return TestLoader.LoadFromFile<Dictionary<string, HexPrefixTestJson>, HexPrefixTest>(
                "hexencodetest.json",
                c => c.Select(p => new HexPrefixTest(p.Key, p.Value.Seq, p.Value.Term, p.Value.Out)));
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(HexPrefixTest test)
        {
            Nevermind.Core.Encoding.HexPrefix result =
                new Nevermind.Core.Encoding.HexPrefix(test.IsTerm, test.Sequence);
            byte[] bytes = result.ToBytes();
            string resultHex = Hex.FromBytes(bytes, false);
            Assert.AreEqual(test.Output, resultHex);

            Nevermind.Core.Encoding.HexPrefix check = Nevermind.Core.Encoding.HexPrefix.FromBytes(bytes);
            byte[] checkBytes = check.ToBytes();
            string checkHex = Hex.FromBytes(checkBytes, false);
            Assert.AreEqual(test.Output, checkHex);
        }

        [UsedImplicitly]
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