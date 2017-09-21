using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Nevermind.Core.Encoding;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.HexPrefix.Test
{
    public class HexPrefixTests
    {
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

        private static IEnumerable<HexPrefixTest> LoadTests()
        {
            Assembly assembly = typeof(HexPrefixTestJson).Assembly;
            string[] resourceNames = Assembly.GetExecutingAssembly().GetManifestResourceNames();
            string resourceName = resourceNames.SingleOrDefault(r => r.Contains("hexencodetest.json"));
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                Assert.NotNull(stream);
                using (StreamReader reader = new StreamReader(stream))
                {
                    string testJson = reader.ReadToEnd();
                    Dictionary<string, HexPrefixTestJson> testSpecs =
                        JsonConvert.DeserializeObject<Dictionary<string, HexPrefixTestJson>>(testJson);
                    return testSpecs.Select(p => new HexPrefixTest(p.Key, p.Value.Seq, p.Value.Term, p.Value.Out));
                }
            }
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

        // https://easythereentropy.wordpress.com/2014/06/04/understanding-the-ethereum-trie/
        [Test]
        public void Tutorial_test()
        {
            Nevermind.Core.Encoding.HexPrefix hexPrefix = new Nevermind.Core.Encoding.HexPrefix(true, new byte[] { 1, 1, 2 });
            byte[] result = hexPrefix.ToBytes();
        }
    }
}
