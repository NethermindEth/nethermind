using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using JetBrains.Annotations;
using Nevermind.Core.Encoding;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;

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

        private static IEnumerable<RlpTest> LoadTests(string testFileName)
        {
            Assembly assembly = typeof(RlpTests).Assembly;
            string[] resourceNames = Assembly.GetExecutingAssembly().GetManifestResourceNames();
            string resourceName = resourceNames.SingleOrDefault(r => r.Contains(testFileName));
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                Assert.NotNull(stream);
                using (StreamReader reader = new StreamReader(stream))
                {
                    string testJson = reader.ReadToEnd();
                    Dictionary<string, RlpTestJson> testSpecs =
                        JsonConvert.DeserializeObject<Dictionary<string, RlpTestJson>>(testJson);
                    return testSpecs.Select(p => new RlpTest(p.Key, p.Value.In, p.Value.Out));
                }
            }
        }

        private static IEnumerable<RlpTest> LoadInvalidTests()
        {
            return LoadTests("invalidRLPTest.json");
        }

        private object PrepareInput(object input)
        {
            string s = input as string;
            if (s != null && s.StartsWith("#"))
            {
                BigInteger bigInteger = BigInteger.Parse(s.Substring(1));
                input = bigInteger;
            }

            if (input is JArray)
            {
                input = ((JArray)input).Select(PrepareInput).ToArray();
            }

            JToken token = input as JToken;
            if (token != null)
            {
                if (token.Type == JTokenType.String)
                {
                    return token.Value<string>();
                }

                if (token.Type == JTokenType.Integer)
                {
                    return token.Value<long>();
                }
            }

            return input;
        }

        [TestCaseSource(nameof(LoadValidTests))]
        public void Test(RlpTest test)
        {
            object input = PrepareInput(test.Input);

            Nevermind.Core.Encoding.Rlp serialized = Nevermind.Core.Encoding.Rlp.Serialize(input);
            string serializedHex = serialized.ToString(false);

            object deserialized = Nevermind.Core.Encoding.Rlp.Deserialize(serialized);
            Nevermind.Core.Encoding.Rlp serializedAgain = Nevermind.Core.Encoding.Rlp.Serialize(deserialized);
            string serializedAgainHex = serializedAgain.ToString(false);

            Assert.AreEqual(test.Output, serializedHex);
            Assert.AreEqual(serializedHex, serializedAgainHex);
        }

        [TestCaseSource(nameof(LoadInvalidTests))]
        public void TestInvalid(RlpTest test)
        {
            Nevermind.Core.Encoding.Rlp invalidBytes = new Nevermind.Core.Encoding.Rlp(Hex.ToBytes(test.Output));
            Assert.Throws<InvalidOperationException>(
                () => Nevermind.Core.Encoding.Rlp.Deserialize(invalidBytes));
        }

        [TestCaseSource(nameof(LoadRandomTests))]
        public void TestRandom(RlpTest test)
        {
            Nevermind.Core.Encoding.Rlp validBytes = new Nevermind.Core.Encoding.Rlp(Hex.ToBytes(test.Output));
            Nevermind.Core.Encoding.Rlp.Deserialize(validBytes);
        }
    }
}