// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.HighPerformance;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.All)]
    public class PatriciaTreeTests(bool useFullTrieStore)
    {
        [Test]
        public void Create_commit_change_balance_get()
        {
            Account account = new(1);
            using ITrieStore trieStore = CreateTrieStore();
            using var _ = trieStore.BeginBlockCommit(0);
            StateTree stateTree = new(trieStore.GetTrieStore(null), LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();

            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();

            Account accountRestored = stateTree.Get(TestItem.AddressA);
            Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
        }

        [Test]
        public void Create_create_commit_change_balance_get()
        {
            Account account = new(1);
            using ITrieStore trieStore = CreateTrieStore();
            StateTree stateTree = new(trieStore.GetTrieStore(null), LimboLogs.Instance);

            {
                using var _ = trieStore.BeginBlockCommit(0);
                stateTree.Set(TestItem.AddressA, account);
                stateTree.Set(TestItem.AddressB, account);
                stateTree.Commit();

                account = account.WithChangedBalance(2);
                stateTree.Set(TestItem.AddressA, account);
                stateTree.Commit();
            }

            Account accountRestored = stateTree.Get(TestItem.AddressA);
            Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
        }

        [Test]
        public void Create_commit_reset_change_balance_get()
        {
            if (useFullTrieStore) Assert.Ignore("immediate key count does not work with pruning try store");

            MemDb db = new();
            Account account = new(1);
            using ITrieStore trieStore = CreateTrieStore(db);

            {
                using var _ = trieStore.BeginBlockCommit(0);
                StateTree stateTree = new(trieStore.GetTrieStore(null), LimboLogs.Instance);
                stateTree.Set(TestItem.AddressA, account);
                stateTree.Commit();

                Hash256 rootHash = stateTree.RootHash;
                stateTree.RootHash = null;

                stateTree.RootHash = rootHash;
                stateTree.Get(TestItem.AddressA);
                account = account.WithChangedBalance(2);
                stateTree.Set(TestItem.AddressA, account);
                stateTree.Commit();
            }

            Assert.That(db.Keys.Count, Is.EqualTo(2));
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void Commit_with_skip_root_should_skip_root(bool skipRoot, bool hasRoot)
        {
            using ITrieStore fullTrieStore = CreateTrieStore();
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
            Account account = new(1);

            Hash256 stateRoot;
            {
                using var _ = fullTrieStore.BeginBlockCommit(0);
                StateTree stateTree = new(trieStore, LimboLogs.Instance);
                stateTree.Set(TestItem.AddressA, account);
                stateTree.UpdateRootHash();
                stateRoot = stateTree.RootHash;
                stateTree.Commit(skipRoot);
            }

            fullTrieStore.HasRoot(stateRoot).Should().Be(hasRoot);
        }

        [Test]
        public void Modify_LeafOnlyNode_And_RecalculateRoot()
        {
            using ITrieStore fullTrieStore = CreateTrieStore();
            IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

            PatriciaTree tree = new(trieStore, LimboLogs.Instance);
            tree.Set(new ValueHash256("2222222222222222222222222222222222222222222222222222222222222222").BytesAsSpan,
                [1]);
            tree.UpdateRootHash();

            Hash256 rootHash = tree.RootHash;

            tree.Set(new ValueHash256("2222222222222222222222222222222222222222222222222222222222222222").BytesAsSpan,
                [2]);
            tree.UpdateRootHash();

            tree.RootHash.Should().NotBe(rootHash);
        }

        public static IEnumerable<TestCaseData> NewBranches()
        {
            Random rng = new(0);

            yield return new TestCaseData(GenRandomOfLength(1)).SetName("1");
            yield return new TestCaseData(GenRandomOfLength(1)).SetName("2");
            yield return new TestCaseData(GenRandomOfLength(10)).SetName("10");
            yield return new TestCaseData(GenRandomOfLength(100)).SetName("100");
            yield return new TestCaseData(GenRandomOfLength(1000)).SetName("1000");

            yield return new TestCaseData(new List<(Hash256 key, SpanSource value)>()
            {
                (new Hash256("0000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("1000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("2000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("3000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("4000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("5000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("6000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("7000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("8000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("9000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("b000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("c000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("d000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("e000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("f000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            }).SetName("top level branch");

            yield return new TestCaseData(new List<(Hash256 key, SpanSource value)>()
            {
                (new Hash256("a000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a100000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a200000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a300000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a400000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a500000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a600000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a700000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a800000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a900000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("aa00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("ab00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("ac00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("ad00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("ae00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("af00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            }).SetName("second level branch");

            yield return new TestCaseData(new List<(Hash256 key, SpanSource value)>()
            {
                (new Hash256("a000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("a100000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("f000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("f100000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("f200000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            }).SetName("multi last hex");

            yield return new TestCaseData(new List<(Hash256 key, SpanSource value)>()
            {
                (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("bbbbdddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("cccccccc00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            }).SetName("deep value");
        }

        public static IEnumerable<TestCaseData> PreExistingData()
        {
            Random rng = new(0);

            yield return new TestCaseData(new List<(Hash256 key, SpanSource value)>()).SetName("baseline");

            yield return new TestCaseData(new List<(Hash256 key, SpanSource value)>()
            {
                (new Hash256("3333333333333333333333333333333333333333333333333333333333333333"), MakeRandomValue(rng)),
            }).SetName("one long leaf");

            yield return new TestCaseData(new List<(Hash256 key, SpanSource value)>()
            {
                (new Hash256("3333333333333333333333333333333333333333333333333333333333333333"), MakeRandomValue(rng)),
                (new Hash256("3322222222222222222222222222222222222222222222222222222222222222"), MakeRandomValue(rng)),
            }).SetName("one extension");
        }

        public static IEnumerable<TestCaseData> BulkSetTestGen()
        {
            Random rng = new(0);

            foreach (TestCaseData existingData in PreExistingData())
            {
                foreach (TestCaseData testCaseData in NewBranches())
                {
                    yield return new TestCaseData(existingData.Arguments[0], testCaseData.Arguments[0]).SetName(existingData.TestName + " and " + testCaseData.TestName);
                }
            }

            yield return new TestCaseData(
                new List<(Hash256 key, SpanSource value)>()
                {
                    (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                },
                new List<(Hash256 key, SpanSource value)>()
                {
                    (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), null),
                }
            ).SetName("simple delete");

            yield return new TestCaseData(
                new List<(Hash256 key, SpanSource value)>()
                {
                    (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("bbbbdddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("cccccccc00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng))
                },
                new List<(Hash256 key, SpanSource value)>()
                {
                    (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng))
                }
            ).SetName("replace");

            yield return new TestCaseData(
                new List<(Hash256 key, SpanSource value)>()
                {
                    (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("bbbbdddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("cccccccc00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng))
                },
                new List<(Hash256 key, SpanSource value)>()
                {
                    (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                    (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng))
                }
            ).SetName("replace");


            yield return new TestCaseData(GenRandomOfLength(100), GenRandomOfLength(100)).SetName("reapply");
            for (int i = 0; i < 10; i++)
            {
                yield return new TestCaseData(GenRandomOfLength(1000, i), GenRandomOfLength(1000, i*2)).SetName($"random {i}");
            }

            List<(Hash256 key, SpanSource value)> list = GenRandomOfLength(100);
            List<(Hash256 key, SpanSource value)> eraseList = list.Select<(Hash256 key, SpanSource value), (Hash256 key, SpanSource value)>((k) => (k.key, null)).ToList();

            yield return new TestCaseData(GenRandomOfLength(100), eraseList).SetName("delete");

        }

        static byte[] MakeRandomValue(Random rng)
        {
            if (rng.NextDouble() < 0.05)
            {
                return null;
            }
            if (rng.NextDouble() < 0.05)
            {
                return [];
            }
            byte[] randData = new byte[32];
            rng.NextBytes(randData);
            return randData;
        }

        private static List<(Hash256 key, SpanSource value)> GenRandomOfLength(int itemCount, int seed = 0)
        {
            Random rng = new Random(seed);
            List<(Hash256 key, SpanSource value)> items = new List<(Hash256 key, SpanSource value)>(0);

            for (int i = 0; i < itemCount; i++)
            {
                byte[] buffer = new byte[32];
                rng.NextBytes(buffer);
                Hash256 key = new Hash256(buffer);
                rng.NextBytes(buffer);

                items.Add((key,  new SpanSource(buffer.AsSpan().ToArray())));
            }

            return items;
        }

#pragma warning disable CS0162 // Unreachable code detected
        [TestCaseSource(nameof(BulkSetTestGen))]
        public void BulkSet(List<(Hash256 key, SpanSource value)> existingItems, List<(Hash256 key, SpanSource value)> items)
        {
            Hash256 root;
            const bool recordDump = true;
            String originalDump = "";

            TimeSpan baselineTime;
            {
                IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
                PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
                pTree.RootHash = Keccak.EmptyTreeHash;

                foreach (var existingItem in existingItems)
                {
                    pTree.Set(existingItem.key.Bytes, existingItem.value);
                }
                pTree.Commit();

                long sw = Stopwatch.GetTimestamp();

                foreach (var valueTuple in items) pTree.Set(valueTuple.key.Bytes, valueTuple.value);

                pTree.UpdateRootHash();

                baselineTime = Stopwatch.GetElapsedTime(sw);

                if (recordDump)
                {
                    pTree.Commit();
                    TreeDumper td = new TreeDumper();
                    pTree.Accept(td, pTree.RootHash);
                    originalDump = td.ToString();
                }
                root = pTree.RootHash;
            }

            TimeSpan newTime;
            {
                IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
                PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
                pTree.RootHash = Keccak.EmptyTreeHash;

                foreach (var existingItem in existingItems)
                {
                    pTree.Set(existingItem.key.Bytes, existingItem.value);
                }

                pTree.Commit();

                long sw = Stopwatch.GetTimestamp();

                using ArrayPoolList<(TreePath, SpanSource)> entries = new ArrayPoolList<(TreePath, SpanSource)>(items.Count);
                foreach (var valueTuple in items)
                {
                    entries.Add((new TreePath(valueTuple.key, 64), valueTuple.value));
                }
                entries.Sort((it1, it2) => it1.Item1.CompareTo(it2.Item1));

                pTree.BulkSet(entries.AsSpan());
                pTree.UpdateRootHash();
                newTime = Stopwatch.GetElapsedTime(sw);

                if (recordDump)
                {
                    TreeDumper td = new TreeDumper();
                    pTree.Commit();
                    pTree.Accept(td, pTree.RootHash);
                    if (pTree.RootHash != root)
                    {
                        TestContext.Error.WriteLine($"Baseline {originalDump}");
                        TestContext.Error.WriteLine($"But got {td.ToString()}");
                    }
                }

                pTree.RootHash.Should().Be(root);
            }
            TestContext.Error.WriteLine($"Time is {newTime} vs {baselineTime}");
        }
#pragma warning restore CS0162 // Unreachable code detected


        public static IEnumerable<TestCaseData> HexarySearchTestCases()
        {
            yield return new TestCaseData(
                new List<Hash256>()
                {
                    new Hash256("1111111111111111111111111111111111111111111111111111111111111111"),
                    new Hash256("1111111111111111111111111111111111111111111111111111111111111111"),
                    new Hash256("1111111111111111111111111111111111111111111111111111111111111111"),
                    new Hash256("4111111111111111111111111111111111111111111111111111111111111111"),
                    new Hash256("4111111111111111111111111111111111111111111111111111111111111111"),
                    new Hash256("4111111111111111111111111111111111111111111111111111111111111111"),
                },
                new (int, int)[]
                {
                    ( 1, 0 ),
                    ( 4, 3 )
                }).SetName("base");
        }

        [TestCaseSource(nameof(HexarySearchTestCases))]
        public void HexarySearch(List<Hash256> paths, (int, int)[] expectedResult)
        {
            List<(TreePath, SpanSource)> items = new List<(TreePath, SpanSource)>(paths.Count);
            foreach (Hash256 hash256 in paths)
            {
                items.Add((new TreePath(hash256, 64), SpanSource.Empty));
            }

            Span<(int, int)> result = stackalloc (int, int)[TrieNode.BranchesCount];
            int resultNum = PatriciaTree.HexarySearch(items.AsSpan(), 0, result);

            (int,int)[] asArray = new (int, int)[resultNum];
            for (int i = 0; i < resultNum; i++)
            {
                asArray[i] = (result[i].Item1, result[i].Item2);
            }

            asArray.Should().BeEquivalentTo(expectedResult);
        }

        private ITrieStore CreateTrieStore(IDb db = null)
        {
            db ??= new MemDb();
            return useFullTrieStore
                ? TestTrieStoreFactory.Build(db, LimboLogs.Instance)
                : new TestRawTrieStore(new NodeStorage(db));
        }
    }
}
