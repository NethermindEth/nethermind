using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nevermind.Core.Encoding;
using Nevermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.Trie.Test
{
    public class TrieTests
    {
        private static IEnumerable<TrieTest> LoadAnyOrderTests()
        {
            return LoadTests<Dictionary<string, TrieTestJson>>("trieanyorder_secureTrie.json",
                dwj => dwj.Select(p => new TrieTest(p.Key, p.Value.In.ToList(), p.Value.Root)));
        }

        private static IEnumerable<TrieTest> LoadTests<TContainer>(string testFileName,
            Func<TContainer, IEnumerable<TrieTest>> testExtractor)
        {
            Assembly assembly = typeof(TrieTest).Assembly;
            string[] resourceNames = Assembly.GetExecutingAssembly().GetManifestResourceNames();
            string resourceName = resourceNames.SingleOrDefault(r => r.Contains(testFileName));
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                Assert.NotNull(stream);
                using (StreamReader reader = new StreamReader(stream))
                {
                    string testJson = reader.ReadToEnd();
                    TContainer testSpecs =
                        JsonConvert.DeserializeObject<TContainer>(testJson);
                    return testExtractor(testSpecs);
                }
            }
        }

        [TestCaseSource(nameof(LoadAnyOrderTests))]
        public void Test_any_order(TrieTest test)
        {
            Db db = new Db();
            PatriciaTree patriciaTree = new PatriciaTree(db);
            foreach (KeyValuePair<string, string> keyValuePair in test.Input)
            {
                patriciaTree.Set(Encoding.ASCII.GetBytes(keyValuePair.Key), Encoding.ASCII.GetBytes(keyValuePair.Key));
                Assert.AreEqual(test.ExpectedRoot, patriciaTree.Root);
            }
        }

        // https://easythereentropy.wordpress.com/2014/06/04/understanding-the-ethereum-trie/
        [Test]
        public void Tutorial_test_1()
        {
            Db db = new Db();
            PatriciaTree patriciaTree = new PatriciaTree(db);
            patriciaTree.Set(
                new byte[] { 1, 1, 2 },
                RecursiveLengthPrefix.Serialize(new object[] { "hello" }));

            Assert.AreEqual("0x15da97c42b7ed2e1c0c8dab6a6d7e3d9dc0a75580bbc4f1f29c33996d1415dcc", patriciaTree.RootHash.ToString());
        }

        [Test]
        public void Tutorial_test_2()
        {
            Db db = new Db();
            PatriciaTree patriciaTree = new PatriciaTree(db);
            patriciaTree.Set(
                new byte[] { 1, 1, 2 },
                RecursiveLengthPrefix.Serialize(new object[] { "hello" }));

            PatriciaTree another = new PatriciaTree(patriciaTree.RootHash, db);
            Assert.AreEqual(Keccak.Compute(((LeafNode)(patriciaTree.Root)).Key), Keccak.Compute(((LeafNode)(another.Root)).Key));
            Assert.AreEqual(Keccak.Compute(((LeafNode)(patriciaTree.Root)).Value), Keccak.Compute(((LeafNode)(another.Root)).Value));
        }

        [Test]
        public void Tutorial_test_3()
        {
            Db db = new Db();
            PatriciaTree patriciaTree = new PatriciaTree(db);
            patriciaTree.Set(
                new byte[] { 1, 1, 2 },
                RecursiveLengthPrefix.Serialize(new object[] { "hello" }));

            patriciaTree.Set(
                new byte[] { 1, 1, 2 },
                RecursiveLengthPrefix.Serialize(new object[] { "hellothere" }));

            Assert.AreEqual("0x05e13d8be09601998499c89846ec5f3101a1ca09373a5f0b74021261af85d396", patriciaTree.RootHash.ToString());
        }

        [Test]
        public void Tutorial_test_4()
        {
            Db db = new Db();
            PatriciaTree patriciaTree = new PatriciaTree(db);
            patriciaTree.Set(
                new byte[] { 1, 1, 2 },
                RecursiveLengthPrefix.Serialize(new object[] { "hello" }));

            patriciaTree.Set(
                new byte[] { 1, 1, 3 },
                RecursiveLengthPrefix.Serialize(new object[] { "hellothere" }));

            ExtensionNode extension = patriciaTree.Root as ExtensionNode;
            Assert.NotNull(extension);
            BranchNode branch = patriciaTree.RlpDecode(db[extension.NextNodeHash]) as BranchNode;
            Assert.NotNull(branch);
            Assert.AreEqual(5, db.Count);
        }

        public class TrieTestJson
        {
            public Dictionary<string, string> In { get; set; }
            public string Root { get; set; }
        }

        public class TrieTest
        {
            public TrieTest(string name, IReadOnlyCollection<KeyValuePair<string, string>> input, string expectedRoot)
            {
                Name = name;
                Input = input;
                ExpectedRoot = expectedRoot;
            }

            public string Name { get; set; }
            public IReadOnlyCollection<KeyValuePair<string, string>> Input { get; set; }
            public string ExpectedRoot { get; set; }

            public override string ToString()
            {
                return $"{Name}, exp: {ExpectedRoot}";
            }
        }
    }
}