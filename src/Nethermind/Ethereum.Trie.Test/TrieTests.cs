// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ethereum.Test.Base;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Ethereum.Trie.Test
{
    public class TrieTests
    {
        private MemDb _db;

        [SetUp]
        public void Setup()
        {
            TrieNode.AllowBranchValues = true;
            _db = new MemDb();
        }

        private static IEnumerable<TrieTest> GetTestPermutations(IEnumerable<TrieTest> tests)
        {
            return tests.SelectMany(t =>
            {
                List<TrieTest> permutations = new List<TrieTest>();
                Permutations.ForAllPermutation(t.Input.ToArray(), p =>
                {
                    permutations.Add(new TrieTest(t.Name, p.ToArray(), t.ExpectedRoot));
                    return false;
                });

                return permutations;
            });
        }

        private static TrieTest Convert(KeyValuePair<string, TrieTestArraysJson> p)
        {
            return new TrieTest(
                p.Key,
                p.Value.In.Select(entry => new KeyValuePair<string, string>(entry[0], entry[1] ?? string.Empty))
                    .ToList(),
                p.Value.Root);
        }

        private static IEnumerable<TrieTest> LoadTests()
        {
            return TestLoader.LoadFromFile<Dictionary<string, TrieTestArraysJson>, TrieTest>(
                "trietest.json",
                dwj => dwj.Select(Convert));
        }

        private static IEnumerable<TrieTest> LoadSecureTests()
        {
            return TestLoader.LoadFromFile<Dictionary<string, TrieTestArraysJson>, TrieTest>(
                "trietest_secureTrie.json",
                dwj => dwj.Select(Convert));
        }

        private static IEnumerable<TrieTest> LoadAnyOrderTests()
        {
            IEnumerable<TrieTest> tests = TestLoader.LoadFromFile<Dictionary<string, TrieTestJson>, TrieTest>(
                "trieanyorder.json",
                dwj => dwj.Select(p => new TrieTest(p.Key, p.Value.In.ToList(), p.Value.Root)));
            return GetTestPermutations(tests);
        }

        private static IEnumerable<TrieTest> LoadHexEncodedSecureTests()
        {
            IEnumerable<TrieTest> tests = TestLoader.LoadFromFile<Dictionary<string, TrieTestJson>, TrieTest>(
                "hex_encoded_securetrie_test.json",
                dwj => dwj.Select(p => new TrieTest(p.Key, p.Value.In.ToList(), p.Value.Root)));
            return GetTestPermutations(tests);
        }

        private static IEnumerable<TrieTest> LoadAnyOrderSecureTests()
        {
            IEnumerable<TrieTest> tests = TestLoader.LoadFromFile<Dictionary<string, TrieTestJson>, TrieTest>(
                "trieanyorder_secureTrie.json",
                dwj => dwj.Select(p => new TrieTest(p.Key, p.Value.In.ToList(), p.Value.Root)));
            return GetTestPermutations(tests);
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(TrieTest test)
        {
            RunTest(test, false);
        }

        [TestCaseSource(nameof(LoadSecureTests))]
        public void Test_secure(TrieTest test)
        {
            RunTest(test, true);
        }

        [TestCaseSource(nameof(LoadAnyOrderTests))]
        public void Test_any_order(TrieTest test)
        {
            RunTest(test, false);
        }

        [TestCaseSource(nameof(LoadAnyOrderSecureTests))]
        public void Test_any_order_secure(TrieTest test)
        {
            RunTest(test, true);
        }

        [TestCaseSource(nameof(LoadHexEncodedSecureTests))]
        public void Test_hex_encoded_secure(TrieTest test)
        {
            RunTest(test, true);
        }

        private void RunTest(TrieTest test, bool secure)
        {
            if (secure)
            {
                // removed the implementation of secure trie as it was not used outside of tests
                return;
            }

            string permutationDescription =
                string.Join(Environment.NewLine, test.Input.Select(p => $"{p.Key} -> {p.Value}"));

            TestContext.WriteLine(Surrounded(permutationDescription));

            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, false, true, NullLogManager.Instance);
            foreach (KeyValuePair<string, string> keyValuePair in test.Input)
            {
                string keyString = keyValuePair.Key;
                string valueString = keyValuePair.Value;

                Nibble[] key = keyString.StartsWith("0x")
                    ? Nibbles.FromHexString(keyString)
                    : Nibbles.FromBytes(Encoding.ASCII.GetBytes(keyString));

                byte[] value = valueString.StartsWith("0x")
                    ? Bytes.FromHexString(valueString)
                    : Encoding.ASCII.GetBytes(valueString);

                TestContext.WriteLine();
                TestContext.WriteLine($"Setting {keyString} -> {valueString}");
                patriciaTree.Set(key.ToPackedByteArray(), value);
            }

            patriciaTree.UpdateRootHash();
            Assert.AreEqual(test.ExpectedRoot, patriciaTree.RootHash.ToString());
        }

        public string Surrounded(string text)
        {
            return string.Concat(Environment.NewLine, text, Environment.NewLine);
        }

        ///// <summary>
        /////     https://easythereentropy.wordpress.com/2014/06/04/understanding-the-ethereum-trie/
        ///// </summary>
        //[Test]
        //public void Tutorial_test_1()
        //{
        //    PatriciaTree patriciaTree = new PatriciaTree(_db);
        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02 },
        //        Rlp.Encode(new object[] { "hello" }));

        //    patriciaTree.UpdateRootHash();
        //    Assert.AreEqual("0x15da97c42b7ed2e1c0c8dab6a6d7e3d9dc0a75580bbc4f1f29c33996d1415dcc",
        //        patriciaTree.RootHash.ToString());
        //}

        //[Test]
        //public void Tutorial_test_1_keccak()
        //{
        //    PatriciaTree patriciaTree = new PatriciaTree(_db);
        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02 },
        //        Rlp.Encode(new object[] { "hello" }));

        //    patriciaTree.Commit();
        //    PatriciaTree another = new PatriciaTree(_db, patriciaTree.RootHash);
        //    Assert.AreEqual(((Leaf)patriciaTree.Root).Key.ToString(), ((Leaf)another.Root).Key.ToString());
        //    Assert.AreEqual(Keccak.Compute(((Leaf)patriciaTree.Root).Value),
        //        Keccak.Compute(((Leaf)another.Root).Value));
        //}

        //[Test]
        //public void Tutorial_test_2()
        //{
        //    PatriciaTree patriciaTree = new PatriciaTree(_db);
        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02 },
        //        Rlp.Encode(new object[] { "hello" }));

        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02 },
        //        Rlp.Encode(new object[] { "hellothere" }));

        //    patriciaTree.Commit();
        //    Assert.AreEqual("0x05e13d8be09601998499c89846ec5f3101a1ca09373a5f0b74021261af85d396",
        //        patriciaTree.RootHash.ToString());
        //}

        //[Test]
        //public void Tutorial_test_2b()
        //{
        //    PatriciaTree patriciaTree = new PatriciaTree(_db);
        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02 },
        //        Rlp.Encode(new object[] { "hello" }));

        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x03 },
        //        Rlp.Encode(new object[] { "hellothere" }));

        //    Extension extension = patriciaTree.Root as Extension;
        //    Assert.NotNull(extension);
        //    Branch branch = extension.NextNodeRef?.Node as Branch;
        //    Assert.NotNull(branch);

        //    patriciaTree.UpdateRootHash();
        //    Assert.AreEqual("0xb5e187f15f1a250e51a78561e29ccfc0a7f48e06d19ce02f98dd61159e81f71d",
        //        patriciaTree.RootHash.ToString());
        //}

        //[Test]
        //public void Tutorial_test_2c()
        //{
        //    PatriciaTree patriciaTree = new PatriciaTree(_db);
        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02 },
        //        Rlp.Encode(new object[] { "hello" }));

        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01 },
        //        Rlp.Encode(new object[] { "hellothere" }));

        //    Extension extension = patriciaTree.Root as Extension;
        //    Assert.NotNull(extension);
        //    Branch branch = extension.NextNodeRef?.Node as Branch;
        //    Assert.NotNull(branch);
        //}

        //[Test]
        //public void Tutorial_test_2d()
        //{
        //    PatriciaTree patriciaTree = new PatriciaTree(_db);
        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02 },
        //        Rlp.Encode(new object[] { "hello" }));

        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02, 0x55 },
        //        Rlp.Encode(new object[] { "hellothere" }));

        //    Extension extension = patriciaTree.Root as Extension;
        //    Assert.NotNull(extension);
        //    Branch branch = extension.NextNodeRef?.Node as Branch;
        //    Assert.NotNull(branch);

        //    patriciaTree.UpdateRootHash();
        //    Assert.AreEqual("0x17fe8af9c6e73de00ed5fd45d07e88b0c852da5dd4ee43870a26c39fc0ec6fb3",
        //        patriciaTree.RootHash.ToString());
        //}

        //[Test]
        //public void Tutorial_test_3()
        //{
        //    PatriciaTree patriciaTree = new PatriciaTree(_db);
        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02 },
        //        Rlp.Encode(new object[] { "hello" }));

        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02, 0x55 },
        //        Rlp.Encode(new object[] { "hellothere" }));

        //    patriciaTree.Set(
        //        new byte[] { 0x01, 0x01, 0x02, 0x57 },
        //        Rlp.Encode(new object[] { "jimbojones" }));

        //    patriciaTree.Commit();
        //    Assert.AreEqual("0xfcb2e3098029e816b04d99d7e1bba22d7b77336f9fe8604f2adfb04bcf04a727",
        //        patriciaTree.RootHash.ToString());
        //}

        [Test]
        public void Quick_empty()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, false, true, NullLogManager.Instance);
            Assert.AreEqual(PatriciaTree.EmptyTreeHash, patriciaTree.RootHash);
        }

        [Test]
        public void Delete_on_empty()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, false, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1").Bytes, new byte[0]);
            patriciaTree.Commit(0);
            Assert.AreEqual(PatriciaTree.EmptyTreeHash, patriciaTree.RootHash);
        }

        [Test]
        public void Delete_missing_resolved_on_branch()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, false, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1123").Bytes, new byte[] { 1 });
            patriciaTree.Set(Keccak.Compute("1124").Bytes, new byte[] { 2 });
            Keccak rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(Keccak.Compute("1125").Bytes, new byte[0]);
            Assert.AreEqual(rootBefore, patriciaTree.RootHash);
        }

        [Test]
        public void Delete_missing_resolved_on_extension()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, false, true, NullLogManager.Instance);
            patriciaTree.Set(new Nibble[] { 1, 2, 3, 4 }.ToPackedByteArray(), new byte[] { 1 });
            patriciaTree.Set(new Nibble[] { 1, 2, 3, 4, 5 }.ToPackedByteArray(), new byte[] { 2 });
            patriciaTree.UpdateRootHash();
            Keccak rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(new Nibble[] { 1, 2, 3 }.ToPackedByteArray(), new byte[] { });
            patriciaTree.UpdateRootHash();
            Assert.AreEqual(rootBefore, patriciaTree.RootHash);
        }

        [Test]
        public void Delete_missing_resolved_on_leaf()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, false, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1234567").Bytes, new byte[] { 1 });
            patriciaTree.Set(Keccak.Compute("1234501").Bytes, new byte[] { 2 });
            patriciaTree.UpdateRootHash();
            Keccak rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(Keccak.Compute("1234502").Bytes, new byte[0]);
            patriciaTree.UpdateRootHash();
            Assert.AreEqual(rootBefore, patriciaTree.RootHash);
        }

        [Test]
        public void Lookup_in_empty_tree()
        {
            PatriciaTree tree = new PatriciaTree(new MemDb(), Keccak.EmptyTreeHash, false, true, NullLogManager.Instance);
            Assert.AreEqual(tree.RootRef, null);
            tree.Get(new byte[] { 1 });
            Assert.AreEqual(tree.RootRef, null);
        }

        public class TrieTestJson
        {
            public Dictionary<string, string> In { get; set; }
            public string Root { get; set; }
        }

        public class TrieTestArraysJson
        {
            public string[][] In { get; set; }
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
                return $"{Name}, exp: {ExpectedRoot} {Guid.NewGuid()}";
            }
        }
    }
}
