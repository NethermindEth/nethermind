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
        private readonly ILogger _logger = new TestLogManager().GetClassLogger<TrieTests>();

        [SetUp]
        public void Setup() => _db = new MemDb();

        [TearDown]
        public void TearDown() => _db?.Dispose();

        private static IEnumerable<TrieTest> GetTestPermutations(IEnumerable<TrieTest> tests) =>
            tests.SelectMany(t =>
            {
                List<TrieTest> permutations = [];
                Permutations.ForAllPermutation(t.Input.ToArray(), p =>
                {
                    permutations.Add(new TrieTest(t.Name, p.ToArray(), t.ExpectedRoot));
                    return false;
                });

                return permutations;
            });

        private static TrieTest Convert(KeyValuePair<string, TrieTestArraysJson> p) =>
            new(
                p.Key,
                p.Value.In.Select(entry => new KeyValuePair<string, string>(entry[0], entry[1] ?? string.Empty))
                    .ToList(),
                p.Value.Root);

        private static TrieTest Convert(KeyValuePair<string, TrieTestJson> p) =>
            new(p.Key, p.Value.In.ToList(), p.Value.Root);

        // Filter out branch value tests (keys shorter than 32 bytes)
        private static bool HasOnlyFullKeys(TrieTest t) => t.Input.All(kvp => kvp.Key.Length == 32);

        private static IEnumerable<TrieTest> LoadArrays(string file, bool filterBranch = false)
        {
            IEnumerable<TrieTest> tests = TestLoader.LoadFromFile<Dictionary<string, TrieTestArraysJson>, TrieTest>(
                file, d => d.Select(Convert));
            return filterBranch ? tests.Where(HasOnlyFullKeys) : tests;
        }

        private static IEnumerable<TrieTest> LoadPermuted(string file, bool filterBranch = false)
        {
            IEnumerable<TrieTest> tests = TestLoader.LoadFromFile<Dictionary<string, TrieTestJson>, TrieTest>(
                file, d => d.Select(Convert));
            if (filterBranch) tests = tests.Where(HasOnlyFullKeys);
            return GetTestPermutations(tests);
        }

        private static IEnumerable<TrieTest> LoadAllTests() =>
            LoadArrays("trietest.json", filterBranch: true)
                .Concat(LoadPermuted("trieanyorder.json", filterBranch: true));

        [TestCaseSource(nameof(LoadAllTests))]
        public void Test(TrieTest test)
        {
            string permutationDescription = string.Join(Environment.NewLine, test.Input.Select(p => $"{p.Key} -> {p.Value}"));

            TestContext.Out.WriteLine(Surrounded(permutationDescription));

            PatriciaTree patriciaTree = new(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
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

        private string Surrounded(string text) => string.Concat(Environment.NewLine, text, Environment.NewLine);

        [Test]
        public void Quick_empty()
        {
            PatriciaTree patriciaTree = new(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            Assert.That(patriciaTree.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash));
        }

        [Test]
        public void Delete_on_empty()
        {
            PatriciaTree patriciaTree = new(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1").Bytes, Array.Empty<byte>());
            patriciaTree.Commit();
            Assert.That(patriciaTree.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash));
        }

        [Test]
        public void Delete_missing_resolved_on_branch()
        {
            PatriciaTree patriciaTree = new(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1123").Bytes, [1]);
            patriciaTree.Set(Keccak.Compute("1124").Bytes, [2]);
            Hash256 rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(Keccak.Compute("1125").Bytes, Array.Empty<byte>());
            Assert.That(patriciaTree.RootHash, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Delete_missing_resolved_on_extension()
        {
            PatriciaTree patriciaTree = new(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            patriciaTree.Set(new Nibble[] { 1, 2, 3, 4 }.ToPackedByteArray(), [1]);
            patriciaTree.Set(new Nibble[] { 1, 2, 3, 4, 5 }.ToPackedByteArray(), [2]);
            patriciaTree.UpdateRootHash();
            Hash256 rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(new Nibble[] { 1, 2, 3 }.ToPackedByteArray(), Array.Empty<byte>());
            patriciaTree.UpdateRootHash();
            Assert.That(patriciaTree.RootHash, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Delete_missing_resolved_on_leaf()
        {
            PatriciaTree patriciaTree = new(_db, Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            patriciaTree.Set(Keccak.Compute("1234567").Bytes, [1]);
            patriciaTree.Set(Keccak.Compute("1234501").Bytes, [2]);
            patriciaTree.UpdateRootHash();
            Hash256 rootBefore = patriciaTree.RootHash;
            patriciaTree.Set(Keccak.Compute("1234502").Bytes, Array.Empty<byte>());
            patriciaTree.UpdateRootHash();
            Assert.That(patriciaTree.RootHash, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Lookup_in_empty_tree()
        {
            PatriciaTree tree = new(new MemDb(), Keccak.EmptyTreeHash, true, NullLogManager.Instance);
            Assert.That(tree.RootRef, Is.Null);
            tree.Get([1]);
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

        public class TrieTest(string name, IReadOnlyCollection<KeyValuePair<string, string>> input, string expectedRoot)
        {
            public string Name { get; set; } = name;
            public IReadOnlyCollection<KeyValuePair<string, string>> Input { get; set; } = input;
            public string ExpectedRoot { get; set; } = expectedRoot;
            public override string ToString() => $"{Name}, exp: {ExpectedRoot} {Guid.NewGuid()}";
        }
    }
}
