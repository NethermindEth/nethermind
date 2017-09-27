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
        private Db _db;

        [SetUp]
        public void Setup()
        {
            _db = new Db();
        }

        [TearDown]
        public void TearDown()
        {
            _db.Print(Console.WriteLine);
        }

        private static IEnumerable<TrieTest> LoadAnyOrderTests()
        {
            return LoadTests<Dictionary<string, TrieTestJson>>("trieanyorder.json",
                dwj => dwj.Select(p => new TrieTest(p.Key, p.Value.In.ToList(), p.Value.Root)));
        }

        private static IEnumerable<TrieTest> LoadAnyOrderSecureTests()
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
            PatriciaTree patriciaTree = new PatriciaTree(_db);
            foreach (KeyValuePair<string, string> keyValuePair in test.Input)
            {
                patriciaTree.Set(Encoding.ASCII.GetBytes(keyValuePair.Key), Encoding.ASCII.GetBytes(keyValuePair.Value));
            }

            Assert.AreEqual(test.ExpectedRoot, patriciaTree.RootHash.ToString());
        }

        //[TestCaseSource(nameof(LoadAnyOrderSecureTests))]
        //public void Test_any_order_secure(TrieTest test)
        //{
        //    Db db = new Db();
        //    PatriciaTree patriciaTree = new PatriciaTree(db);
        //    foreach (KeyValuePair<string, string> keyValuePair in test.Input)
        //    {
        //        patriciaTree.Set(Encoding.ASCII.GetBytes(keyValuePair.Key), Encoding.ASCII.GetBytes(keyValuePair.Key));
        //    }

        //    Assert.AreEqual(test.ExpectedRoot, patriciaTree.RootHash.ToString());
        //}

        // https://easythereentropy.wordpress.com/2014/06/04/understanding-the-ethereum-trie/
        [Test]
        public void Tutorial_test_1()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db);
            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02 },
                Rlp.Serialize(new object[] { "hello" }));

            Assert.AreEqual("0x15da97c42b7ed2e1c0c8dab6a6d7e3d9dc0a75580bbc4f1f29c33996d1415dcc", patriciaTree.RootHash.ToString());
        }

        [Test]
        public void Tutorial_test_1_keccak()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db);
            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02 },
                Rlp.Serialize(new object[] { "hello" }));

            PatriciaTree another = new PatriciaTree(patriciaTree.RootHash, _db);
            Assert.AreEqual(((LeafNode)patriciaTree.Root).Key.ToString(), ((LeafNode)another.Root).Key.ToString());
            Assert.AreEqual(Keccak.Compute(((LeafNode)patriciaTree.Root).Value), Keccak.Compute(((LeafNode)another.Root).Value));
        }

        [Test]
        public void Tutorial_test_2()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db);
            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02 },
                Rlp.Serialize(new object[] { "hello" }));

            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02 },
                Rlp.Serialize(new object[] { "hellothere" }));

            Assert.AreEqual("0x05e13d8be09601998499c89846ec5f3101a1ca09373a5f0b74021261af85d396", patriciaTree.RootHash.ToString());
        }

        [Test]
        public void Tutorial_test_2b()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db);
            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02 },
                Rlp.Serialize(new object[] { "hello" }));

            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x03 },
                Rlp.Serialize(new object[] { "hellothere" }));

            ExtensionNode extension = patriciaTree.Root as ExtensionNode;
            Assert.NotNull(extension);
            BranchNode branch = patriciaTree.GetNode(extension.NextNode) as BranchNode;
            Assert.NotNull(branch);

            // branch should have 0x01ab hash
            Assert.AreEqual("0xb5e187f15f1a250e51a78561e29ccfc0a7f48e06d19ce02f98dd61159e81f71d", patriciaTree.RootHash.ToString());
        }

        [Test]
        public void Tutorial_test_2c()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db);
            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02 },
                Rlp.Serialize(new object[] { "hello" }));

            patriciaTree.Set(
                new byte[] { 0x01, 0x01 },
                Rlp.Serialize(new object[] { "hellothere" }));

            ExtensionNode extension = patriciaTree.Root as ExtensionNode;
            Assert.NotNull(extension);
            BranchNode branch = patriciaTree.GetNode(extension.NextNode) as BranchNode;
            Assert.NotNull(branch);
        }

        [Test]
        public void Tutorial_test_2d()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db);
            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02 },
                Rlp.Serialize(new object[] { "hello" }));

            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02, 0x55 },
                Rlp.Serialize(new object[] { "hellothere" }));

            ExtensionNode extension = patriciaTree.Root as ExtensionNode;
            Assert.NotNull(extension);
            BranchNode branch = patriciaTree.GetNode(extension.NextNode) as BranchNode;
            Assert.NotNull(branch);

            Assert.AreEqual("0x17fe8af9c6e73de00ed5fd45d07e88b0c852da5dd4ee43870a26c39fc0ec6fb3", patriciaTree.RootHash.ToString());
        }

        [Test]
        public void Tutorial_test_3()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db);
            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02 },
                Rlp.Serialize(new object[] { "hello" }));

            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02, 0x55 },
                Rlp.Serialize(new object[] { "hellothere" }));

            patriciaTree.Set(
                new byte[] { 0x01, 0x01, 0x02, 0x57 },
                Rlp.Serialize(new object[] { "jimbojones" }));

            // 1st branch should be 0xd5af
            // 2nd branch should be 0x0026
            Assert.AreEqual("0xfcb2e3098029e816b04d99d7e1bba22d7b77336f9fe8604f2adfb04bcf04a727", patriciaTree.RootHash.ToString());
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