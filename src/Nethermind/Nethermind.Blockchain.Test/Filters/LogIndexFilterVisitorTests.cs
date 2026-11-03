// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db.LogIndex;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters;

[Parallelizable(ParallelScope.All)]
public class LogIndexFilterVisitorTests
{
    private const int StorageBlockCount = 6_000;
    private const int StorageBatchSize = 50;
    private static readonly Address SparseAddress = TestItem.AddressA;
    private static readonly Address DenseAddress = TestItem.AddressB;
    private static readonly Address MissingAddress = TestItem.AddressC;
    private static readonly Hash256 DenseTopic = TestItem.KeccakA;
    private static readonly Hash256 SparseTopic = TestItem.KeccakB;

    private ChunkedLogIndexStorage _chunkedIndex = null!;
    private HashSet<int> _sparseAddressBlocks = null!;
    private HashSet<int> _denseAddressBlocks = null!;
    private HashSet<int> _denseTopicBlocks = null!;
    private HashSet<int> _sparseTopicBlocks = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Random random = new(42);
        _sparseAddressBlocks = RandomBlocks(random, 100);
        _denseAddressBlocks = RandomBlocks(random, 2);
        _denseTopicBlocks = RandomBlocks(random, 1);
        _sparseTopicBlocks = RandomBlocks(random, 30);

        _chunkedIndex = await ChunkedLogIndexStorage.CreateAsync(StorageBlockCount, StorageBatchSize, StorageLogsAt);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _chunkedIndex.DisposeAsync();

    private record Ranges(Dictionary<Address, List<int>> Address, Dictionary<Hash256, List<int>>[] Topic)
    {
        public List<int> this[Address address] => Address[address];
        public List<int> this[int topicIndex, Hash256 hash] => Topic[topicIndex][hash];
    }

    public class EnumeratorWrapper(int[] array) : IEnumerator<int>
    {
        private readonly IEnumerator<int> _enumerator = array.Cast<int>().GetEnumerator();
        public bool MoveNext() => _enumerator.MoveNext();
        public void Reset() => _enumerator.Reset();
        public int Current => _enumerator.Current;
        object IEnumerator.Current => Current;
        public virtual void Dispose() => _enumerator.Dispose();
    }

    [TestCase(
        new[] { 1, 3, 5, 7, 9, },
        new[] { 0, 2, 4, 6, 8 },
        TestName = "Non-intersecting, but similar ranges"
    )]
    [TestCase(
        new[] { 1, 2, 3, 4, 5, },
        new[] { 5, 6, 7, 8, 9 },
        TestName = "Intersects on first/last"
    )]
    [TestCase(
        new[] { 1, 2, 3, 4, 5, },
        new[] { 6, 7, 8, 9, 10 },
        TestName = "Non-intersecting ranges"
    )]
    public void IntersectEnumerator(int[] s1, int[] s2)
    {
        int[] expected = s1.Intersect(s2).Order().ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, expected);
    }

    [TestCase(1, 1)]
    [TestCase(20, 20)]
    [TestCase(20, 100)]
    [TestCase(100, 100)]
    [TestCase(1000, 1000)]
    public void IntersectEnumerator_Random(int len1, int len2)
    {
        Random random = new(42);
        int[] s1 = RandomAscending(random, len1, Math.Max(1, len1 / 10));
        int[] s2 = RandomAscending(random, len2, Math.Max(1, len2 / 10));

        int[] expected = s1.Intersect(s2).Order().ToArray();
        Assert.That(expected, Is.Not.Empty, "Unreliable test: Needs non-empty sequence to verify against.");

        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, expected);
    }

    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(0, 10)]
    public void IntersectEnumerator_SomeEmpty(int len1, int len2)
    {
        int[] s1 = Enumerable.Range(0, len1).ToArray();
        int[] s2 = Enumerable.Range(0, len2).ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, []);
        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, []);
    }

    public sealed class SeekableEnumerator(int[] array) : ILogIndexEnumerator
    {
        private int _index = -1;

        public bool MoveNext() => ++_index < array.Length;

        public bool MoveTo(int target)
        {
            if (Current >= target)
                return true;

            int found = Array.BinarySearch(array, _index, array.Length - _index, target);
            _index = found >= 0 ? found : ~found;
            return _index < array.Length;
        }

        public void Reset() => _index = -1;
        public int Current => array[_index];
        object IEnumerator.Current => Current;
        public void Dispose() { }
    }

    [TestCase(1, 1, 1)]
    [TestCase(5, 5_000, 1)]
    [TestCase(50, 5_000, 1)]
    [TestCase(5_000, 5_000, 1)]
    [TestCase(5_000, 5_000, 2)]
    [TestCase(20, 100, 3)]
    public void IntersectEnumerator_Random_DifferentDensity(int len1, int len2, int seed)
    {
        Random random = new(seed);
        int[] s1 = RandomAscending(random, len1, Math.Max(1, 10_000 / len1));
        int[] s2 = RandomAscending(random, len2, Math.Max(1, 10_000 / len2));
        int[] expected = s1.Intersect(s2).Order().ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, expected);
    }

    [TestCase(100, 3_000, 3_000)]
    [TestCase(3_000, 3_000, 3_000)]
    [TestCase(3_000, 100, 50)]
    public void IntersectEnumerator_Nested_Seekable_Random(int len1, int len2, int len3)
    {
        Random random = new(7);
        int[] s1 = RandomAscending(random, len1, Math.Max(1, 6_000 / len1));
        int[] s2 = RandomAscending(random, len2, Math.Max(1, 6_000 / len2));
        int[] s3 = RandomAscending(random, len3, Math.Max(1, 6_000 / len3));
        int[] expected = s1.Intersect(s2).Intersect(s3).Order().ToArray();

        foreach (bool seekableInnerChild in new[] { true, false })
        {
            using LogIndexFilterVisitor.IntersectEnumerator inner = new(new SeekableEnumerator(s2), CreateEnumerator(s3, seekableInnerChild));
            using LogIndexFilterVisitor.IntersectEnumerator outer = new(new SeekableEnumerator(s1), inner);

            Assert.That(EnumerateOnce(outer), Is.EqualTo(expected));
        }
    }

    [Test]
    public void FilterEnumerator_OverChunkedStorage(
        [Values("sparse-dense", "dense-dense", "sparse-sparse", "missing-dense", "dense-dense-sparse", "two-addresses-dense", "sparse-or-topics-sparse")] string query,
        [Values(0, 777, StorageBlockCount / 2 - 5)] int from,
        [Values(StorageBlockCount / 2 + 5, 4_321, StorageBlockCount - 1)] int to)
    {
        (LogFilter Filter, IEnumerable<int> Blocks) testCase = query switch
        {
            "sparse-dense" => (StorageFilter(SparseAddress, DenseTopic), _sparseAddressBlocks.Intersect(_denseTopicBlocks)),
            "dense-dense" => (StorageFilter(DenseAddress, DenseTopic), _denseAddressBlocks.Intersect(_denseTopicBlocks)),
            "sparse-sparse" => (StorageFilter(SparseAddress, null, SparseTopic), _sparseAddressBlocks.Intersect(_sparseTopicBlocks)),
            "missing-dense" => (StorageFilter(MissingAddress, DenseTopic), []),
            "dense-dense-sparse" => (StorageFilter(DenseAddress, DenseTopic, SparseTopic),
                _denseAddressBlocks.Intersect(_denseTopicBlocks).Intersect(_sparseTopicBlocks)),
            "two-addresses-dense" => (FilterBuilder.New()
                    .WithAddresses(SparseAddress, MissingAddress)
                    .WithTopicExpressions(TestTopicExpressions.Specific(DenseTopic))
                    .Build(),
                _sparseAddressBlocks.Intersect(_denseTopicBlocks)),
            "sparse-or-topics-sparse" => (FilterBuilder.New()
                    .WithAddress(SparseAddress)
                    .WithTopicExpressions(TestTopicExpressions.Or(DenseTopic, TestItem.KeccakE), TestTopicExpressions.Specific(SparseTopic))
                    .Build(),
                _sparseAddressBlocks.Intersect(_denseTopicBlocks).Intersect(_sparseTopicBlocks)),
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };

        long[] expected = testCase.Blocks.Where(b => b >= from && b <= to).Order().Select(static b => (long)b).ToArray();

        Assert.That(_chunkedIndex.Storage.EnumerateBlockNumbersFor(testCase.Filter, (ulong)from, (ulong)to).ToArray(), Is.EqualTo(expected));
    }

    [TestCase(
        new[] { 1, 2, 3, 4, 5, },
        new[] { 2, 3, 4 },
        TestName = "Contained"
    )]
    [TestCase(
        new[] { 1, 2, 3, 4, 5, },
        new[] { 1, 2, 3, 4, 5, },
        TestName = "Identical"
    )]
    [TestCase(
        new[] { 1, 3, 5, 7, 9, },
        new[] { 2, 4, 6, 8, 10 },
        TestName = "Complementary"
    )]
    public void UnionEnumerator(int[] s1, int[] s2)
    {
        int[] expected = s1.Union(s2).Distinct().Order().ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s2, s1, expected);
    }

    [TestCase(1, 1)]
    [TestCase(20, 20)]
    [TestCase(20, 100)]
    [TestCase(100, 100)]
    [TestCase(1000, 1000)]
    public void UnionEnumerator_Random(int len1, int len2)
    {
        Random random = new(42);
        int[] s1 = RandomAscending(random, len1, Math.Max(1, len1 / 10));
        int[] s2 = RandomAscending(random, len2, Math.Max(1, len2 / 10));

        int[] expected = s1.Union(s2).Distinct().Order().ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s2, s1, expected);
    }

    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(0, 10)]
    public void UnionEnumerator_SomeEmpty(int len1, int len2)
    {
        int[] s1 = Enumerable.Range(0, len1).ToArray();
        int[] s2 = Enumerable.Range(0, len2).ToArray();

        int[] expected = s1.Union(s2).Distinct().Order().ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s2, s1, expected);
    }

    [TestCaseSource(nameof(FilterTestData))]
    public void FilterEnumerator(string name, LogFilter filter, List<int> expected)
    {
        Assert.That(expected.Count,
            Is.InRange(from: 1, to: ToBlock - FromBlock - 1),
            "Unreliable test: none or all blocks are selected."
        );
        ILogIndexStorage storage = Substitute.For<ILogIndexStorage>();

        foreach ((Address address, List<int> range) in LogIndexRanges.Address)
        {
            storage
                .GetEnumerator(address, Arg.Any<int>(), Arg.Any<int>())
                .Returns(info => range.SkipWhile(x => x < info.ArgAt<int>(1)).TakeWhile(x => x <= info.ArgAt<int>(2)).GetEnumerator());
        }

        for (int i = 0; i < LogIndexRanges.Topic.Length; i++)
        {
            foreach ((Hash256 topic, List<int> range) in LogIndexRanges.Topic[i])
            {
                storage
                    .GetEnumerator(Arg.Is(i), topic, Arg.Any<int>(), Arg.Any<int>())
                    .Returns(info => range.SkipWhile(x => x < info.ArgAt<int>(2)).TakeWhile(x => x <= info.ArgAt<int>(3)).GetEnumerator());
            }
        }

        Assert.That(storage.EnumerateBlockNumbersFor(filter, FromBlock, ToBlock), Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(FilterTestData))]
    public void FilterEnumerator_Dispose(string name, LogFilter filter, List<int> _)
    {
        int[] blockNumbers = [1, 2, 3, 4, 5];
        List<IEnumerator<int>> enumerators = [];

        ILogIndexStorage storage = Substitute.For<ILogIndexStorage>();
        storage.GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>()).Returns(_ => MockEnumerator());
        storage.GetEnumerator(Arg.Any<int>(), Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<int>()).Returns(_ => MockEnumerator());

        storage.EnumerateBlockNumbersFor(filter, FromBlock, ToBlock).ForEach(_ => { });

        enumerators.ForEach(enumerator => enumerator.Received().Dispose());

        IEnumerator<int> MockEnumerator()
        {
            IEnumerator<int>? enumerator = Substitute.ForPartsOf<EnumeratorWrapper>(blockNumbers);
            enumerators.Add(enumerator);
            return enumerator;
        }
    }

    public static IEnumerable FilterTestData
    {
        get
        {
            yield return new TestCaseData(
                "AddressA", // name

                BuildFilter() // filter
                    .WithAddress(TestItem.AddressA)
                    .Build(),

                LogIndexRanges[TestItem.AddressA] // expected range
            );

            yield return new TestCaseData(
                "AddressA or AddressA",

                BuildFilter()
                    .WithAddresses(TestItem.AddressA, TestItem.AddressA)
                    .Build(),

                LogIndexRanges[TestItem.AddressA]
            );

            yield return new TestCaseData(
                "AddressA or AddressB",

                BuildFilter()
                    .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                    .Build(),

                LogIndexRanges[TestItem.AddressA].Union(LogIndexRanges[TestItem.AddressB])
                    .Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Specific(TestItem.KeccakA)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
            );

            yield return new TestCaseData(
                "TopicA or TopicA",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakA)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
            );

            yield return new TestCaseData(
                "TopicA or TopicB",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA].Union(LogIndexRanges[0, TestItem.KeccakB])
                    .Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA, TopicB",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Specific(TestItem.KeccakA),
                        TestTopicExpressions.Specific(TestItem.KeccakB)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
                    .Intersect(LogIndexRanges[1, TestItem.KeccakB])
                    .Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA, -, TopicA",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Specific(TestItem.KeccakA),
                        TestTopicExpressions.Any,
                        TestTopicExpressions.Specific(TestItem.KeccakA)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
                    .Intersect(LogIndexRanges[2, TestItem.KeccakA])
                    .Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA, -, TopicB or TopicC",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Specific(TestItem.KeccakA),
                        TestTopicExpressions.Any,
                        TestTopicExpressions.Or(TestItem.KeccakB, TestItem.KeccakC)
                    ).Build(),
                LogIndexRanges[0, TestItem.KeccakA]
                    .Intersect(LogIndexRanges[2, TestItem.KeccakB].Union(LogIndexRanges[2, TestItem.KeccakC]))
                    .Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "AddressA | TopicA or TopicB, TopicC",

                BuildFilter()
                    .WithAddress(TestItem.AddressA)
                    .WithTopicExpressions(
                        TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB),
                        TestTopicExpressions.Specific(TestItem.KeccakC)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA].Union(LogIndexRanges[0, TestItem.KeccakB])
                    .Intersect(LogIndexRanges[1, TestItem.KeccakC])
                    .Intersect(LogIndexRanges[TestItem.AddressA])
                    .Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "AddressA or AddressB | TopicA or TopicB, -, TopicC, TopicD or TopicE",

                BuildFilter()
                    .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                    .WithTopicExpressions(
                        TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB),
                        TestTopicExpressions.Any,
                        TestTopicExpressions.Specific(TestItem.KeccakC),
                        TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakE)
                    ).Build(),

                LogIndexRanges[TestItem.AddressA].Union(LogIndexRanges[TestItem.AddressB])
                    .Intersect(LogIndexRanges[0, TestItem.KeccakA].Union(LogIndexRanges[0, TestItem.KeccakB]))
                    .Intersect(LogIndexRanges[2, TestItem.KeccakC])
                    .Intersect(LogIndexRanges[3, TestItem.KeccakD].Union(LogIndexRanges[3, TestItem.KeccakE]))
                    .Distinct().Order().ToList()
            );
        }
    }

    private static int[] RandomAscending(Random random, int count, int maxDelta)
    {
        int[] result = new int[count];

        for (int i = 0; i < result.Length; i++)
        {
            int min = i > 0 ? result[i - 1] : -1;
            result[i] = random.Next(min + 1, min + 1 + maxDelta);
        }

        return result;
    }

    private static void VerifyEnumerator<T>(int[] s1, int[] s2, int[] ex)
        where T : IEnumerator<int>
    {
        foreach ((bool seekable1, bool seekable2) in new[] { (false, false), (true, true), (true, false), (false, true) })
        {
            using T enumerator = (T)Activator.CreateInstance(
                typeof(T),
                CreateEnumerator(s1, seekable1),
                CreateEnumerator(s2, seekable2)
            )!;

            Assert.That(EnumerateOnce(enumerator), Is.EqualTo(ex), $"seekable: {seekable1}, {seekable2}");
        }
    }

    private static IEnumerator<int> CreateEnumerator(int[] array, bool seekable) =>
        seekable ? new SeekableEnumerator(array) : array.Cast<int>().GetEnumerator();

    private static IEnumerable<int> EnumerateOnce(IEnumerator<int> enumerator)
    {
        while (enumerator.MoveNext())
            yield return enumerator.Current;
    }

    private const ulong FromBlock = 0;
    private const ulong ToBlock = 99;

    private static readonly Ranges LogIndexRanges = GenerateLogIndexRanges();

    private static Ranges GenerateLogIndexRanges()
    {
        Random random = new(42);

        Dictionary<Address, List<int>> addressRanges = [];
        foreach (Address address in new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE })
        {
            List<int> range = Enumerable.Range((int)FromBlock, (int)(ToBlock + 1)).Where(_ => random.NextDouble() < 0.3).ToList();
            addressRanges.Add(address, range);
        }

        Dictionary<Hash256, List<int>>[] topicRanges = Enumerable
            .Range(0, LogIndexStorage.MaxTopics)
            .Select(_ => new Dictionary<Hash256, List<int>>()).ToArray();

        foreach (Dictionary<Hash256, List<int>> ranges in topicRanges)
        {
            foreach (Hash256 topic in new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD, TestItem.KeccakE })
            {
                List<int> range = Enumerable.Range((int)FromBlock, (int)(ToBlock + 1)).Where(_ => random.NextDouble() < 0.2).ToList();
                ranges.Add(topic, range);
            }
        }

        return new(addressRanges, topicRanges);
    }

    private static FilterBuilder BuildFilter() => FilterBuilder.New()
        .FromBlock(FromBlock)
        .ToBlock(ToBlock);

    private static LogFilter StorageFilter(Address address, Hash256? topic0, Hash256? topic1 = null) => FilterBuilder.New()
        .WithAddress(address)
        .WithTopicExpressions(
            topic0 is null ? TestTopicExpressions.Any : TestTopicExpressions.Specific(topic0),
            topic1 is null ? TestTopicExpressions.Any : TestTopicExpressions.Specific(topic1))
        .Build();

    private static HashSet<int> RandomBlocks(Random random, int oneIn) =>
        [.. Enumerable.Range(0, StorageBlockCount).Where(_ => random.Next(oneIn) == 0)];

    private LogEntry[] StorageLogsAt(int block)
    {
        List<LogEntry> logs = [Build.A.LogEntry.WithAddress(TestItem.AddressD).TestObject];
        if (_sparseAddressBlocks.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(SparseAddress).TestObject);
        if (_denseAddressBlocks.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(DenseAddress).TestObject);
        if (_denseTopicBlocks.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(TestItem.AddressD).WithTopics(DenseTopic).TestObject);
        if (_sparseTopicBlocks.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(TestItem.AddressD).WithTopics(TestItem.KeccakC, SparseTopic).TestObject);
        return [.. logs];
    }
}
