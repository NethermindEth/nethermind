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
using Nethermind.Trie;
using NUnit.Framework;

namespace Ethereum.Trie.Test
{
    public class TrieTests
    {
        private MemDb _db;
        private ILogger _logger = new TestLogManager().GetClassLogger();

        [SetUp]
        public void Setup()
        {
            _db = new MemDb();
        }

        [TearDown]
        public void TearDown() => _db?.Dispose();

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
                dwj => dwj.Select(Convert))
                // Remove branch value tests
                .Where(t => t.Input.All(kvp => kvp.Key.Length == 32));
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
                dwj => dwj.Select(p => new TrieTest(p.Key, p.Value.In.ToList(), p.Value.Root)))
                    // Remove branch value tests
                    .Where(t => t.Input.All(kvp => kvp.Key.Length == 32));
            return GetTestPermutations(tests);
        }

        private static IEnumerable<TrieTest> LoadHexEncodedSecureTests()
        {
            IEnumerable<TrieTest> tests = TestLoader.LoadFromFile<Dictionary<string, TrieTestJson>, TrieTest>(
                "hex_encoded_securetrie_test.json",
                dwj => dwj.Select(p => new TrieTest(p.Key, p.Value.In.ToList(), p.Value.Root)))
                    // Remove branch value tests
                    .Where(t => t.Input.All(kvp => kvp.Key.Length == 32));
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
        public void Test(TrieTest test) => RunTest(test, false);

        [TestCaseSource(nameof(LoadSecureTests))]
        public void Test_secure(TrieTest test) => RunTest(test, true);

        [TestCaseSource(nameof(LoadAnyOrderTests))]
        public void Test_any_order(TrieTest test) => RunTest(test, false);

        [TestCaseSource(nameof(LoadAnyOrderSecureTests))]
        public void Test_any_order_secure(TrieTest test) => RunTest(test, true);

        [TestCaseSource(nameof(LoadHexEncodedSecureTests))]
        public void Test_hex_encoded_secure(TrieTest test) => RunTest(test, true);

        private void RunTest(TrieTest test, bool secure)
        {
            if (secure)
            {
                // removed the implementation of secure trie as it was not used outside of tests
                return;
            }

            string permutationDescription =
                string.Join(Environment.NewLine, test.Input.Select(p => $"{p.Key} -> {p.Value}"));

            TestContext.Out.WriteLine(Surrounded(permutationDescription));

            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
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

                _logger.Info($"\nSetting {keyString} -> {valueString}");
                patriciaTree.Set(key.ToPackedByteArray(), value);
            }

            patriciaTree.UpdateRootHash();
            Assert.That(patriciaTree.RootHash.ToString(), Is.EqualTo(test.ExpectedRoot));
        }

        public string Surrounded(string text)
        {
            return string.Concat(Environment.NewLine, text, Environment.NewLine);
        }

        [Test]
        public void Quick_empty()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            Assert.That(patriciaTree.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash));
        }

        [Test]
        public void Delete_on_empty()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1").Bytes, Array.Empty<byte>());
            patriciaTree.Commit();
            Assert.That(patriciaTree.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash));
        }

        [Test]
        public void Delete_missing_resolved_on_branch()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1123").Bytes, new byte[] { 1 });
            patriciaTree.Set(Keccak.Compute("1124").Bytes, new byte[] { 2 });
            Hash256 rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(Keccak.Compute("1125").Bytes, Array.Empty<byte>());
            Assert.That(patriciaTree.RootHash, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Delete_missing_resolved_on_extension()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            patriciaTree.Set(new Nibble[] { 1, 2, 3, 4 }.ToPackedByteArray(), new byte[] { 1 });
            patriciaTree.Set(new Nibble[] { 1, 2, 3, 4, 5 }.ToPackedByteArray(), new byte[] { 2 });
            patriciaTree.UpdateRootHash();
            Hash256 rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(new Nibble[] { 1, 2, 3 }.ToPackedByteArray(), Array.Empty<byte>());
            patriciaTree.UpdateRootHash();
            Assert.That(patriciaTree.RootHash, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Delete_missing_resolved_on_leaf()
        {
            PatriciaTree patriciaTree = new PatriciaTree(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1234567").Bytes, new byte[] { 1 });
            patriciaTree.Set(Keccak.Compute("1234501").Bytes, new byte[] { 2 });
            patriciaTree.UpdateRootHash();
            Hash256 rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(Keccak.Compute("1234502").Bytes, Array.Empty<byte>());
            patriciaTree.UpdateRootHash();
            Assert.That(patriciaTree.RootHash, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Lookup_in_empty_tree()
        {
            PatriciaTree tree = new PatriciaTree(new MemDb(), Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            Assert.That(tree.RootRef, Is.Null);
            tree.Get(new byte[] { 1 });
            Assert.That(tree.RootRef, Is.Null);
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
