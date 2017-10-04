using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using JetBrains.Annotations;
using Nevermind.Core.Encoding;
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

        private static IEnumerable<RlpTest> LoadValidTests() => LoadTests("rlptest.json");
    
        private static IEnumerable<RlpTest> LoadRandomTests() => LoadTests("example.json");

        private static IEnumerable<RlpTest> LoadInvalidTests() => LoadTests("invalidRLPTest.json");

        private static IEnumerable<RlpTest> LoadTests(string testFileName)
        {
            return TestLoader.LoadFromFile<Dictionary<string, RlpTestJson>, RlpTest>(
                testFileName,
                c => c.Select(p => new RlpTest(p.Key, p.Value.In, p.Value.Out)));
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
    }
}