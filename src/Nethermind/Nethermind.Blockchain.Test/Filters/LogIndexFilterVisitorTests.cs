// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters;

[Parallelizable(ParallelScope.All)]
public class LogIndexFilterVisitorTests
{
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
        var expected = s1.Intersect(s2).Order().ToArray();

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
        var random = new Random(42);
        var s1 = RandomAscending(random, len1, Math.Max(1, len1 / 10));
        var s2 = RandomAscending(random, len2, Math.Max(1, len2 / 10));

        var expected = s1.Intersect(s2).Order().ToArray();
        Assert.That(expected, Is.Not.Empty, "Unreliable test: Needs non-empty sequence to verify against.");

        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, expected);
    }

    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(0, 10)]
    public void IntersectEnumerator_SomeEmpty(int len1, int len2)
    {
        var s1 = Enumerable.Range(0, len1).ToArray();
        var s2 = Enumerable.Range(0, len2).ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, []);
        VerifyEnumerator<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, []);
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
        var expected = s1.Union(s2).Distinct().Order().ToArray();

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
        var random = new Random(42);
        var s1 = RandomAscending(random, len1, Math.Max(1, len1 / 10));
        var s2 = RandomAscending(random, len2, Math.Max(1, len2 / 10));

        var expected = s1.Union(s2).Distinct().Order().ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s2, s1, expected);
    }

    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(0, 10)]
    public void UnionEnumerator_SomeEmpty(int len1, int len2)
    {
        var s1 = Enumerable.Range(0, len1).ToArray();
        var s2 = Enumerable.Range(0, len2).ToArray();

        var expected = s1.Union(s2).Distinct().Order().ToArray();

        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s1, s2, expected);
        VerifyEnumerator<LogIndexFilterVisitor.UnionEnumerator>(s2, s1, expected);
    }

    [TestCaseSource(nameof(FilterTestData))]
    public void FilterEnumerator(string name, LogFilter filter, List<int> expected)
    {
        Assert.That(expected,
            Has.Count.InRange(from: 1, to: ToBlock - FromBlock - 1),
            "Unreliable test: none or all blocks are selected."
        );
        ILogIndexStorage storage = Substitute.For<ILogIndexStorage>();

        foreach ((Address address, List<int> range) in LogIndexRanges.Address)
        {
            storage
                .GetEnumerator(address, Arg.Any<int>(), Arg.Any<int>())
                .Returns(info => range.SkipWhile(x => x < info.ArgAt<int>(1)).TakeWhile(x => x <= info.ArgAt<int>(2)).GetEnumerator());
        }

        for (var i = 0; i < LogIndexRanges.Topic.Length; i++)
        {
            foreach ((Hash256 topic, List<int> range) in LogIndexRanges.Topic[i])
            {
                storage
                    .GetEnumerator(Arg.Is(i), topic, Arg.Any<int>(), Arg.Any<int>())
                    .Returns(info => range.SkipWhile(x => x < info.ArgAt<int>(2)).TakeWhile(x => x <= info.ArgAt<int>(3)).GetEnumerator());
            }
        }

        Assert.That(storage.EnumerateBlockNumbersFor(filter, FromBlock, ToBlock), Is.EquivalentTo(expected));
    }

    [TestCaseSource(nameof(FilterTestData))]
    public void FilterEnumerator_Dispose(string name, LogFilter filter, List<int> _)
    {
        int[] mockedNums = [1, 2, 3, 4, 5];
        List<IEnumerator<int>> enumerators = [];

        ILogIndexStorage storage = Substitute.For<ILogIndexStorage>();
        storage.GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>()).Returns(_ => MockEnumerator());
        storage.GetEnumerator(Arg.Any<int>(), Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<int>()).Returns(_ => MockEnumerator());

        storage.EnumerateBlockNumbersFor(filter, FromBlock, ToBlock).ForEach(_ => { });

        enumerators.ForEach(enumerator => enumerator.Received().Dispose());

        IEnumerator<int> MockEnumerator()
        {
            IEnumerator<int>? enumerator = Substitute.ForPartsOf<EnumeratorWrapper>(mockedNums);
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
        var result = new int[count];

        for (var i = 0; i < result.Length; i++)
        {
            var min = i > 0 ? result[i - 1] : -1;
            result[i] = random.Next(min + 1, min + 1 + maxDelta);
        }

        return result;
    }

    private static void VerifyEnumerator<T>(int[] s1, int[] s2, int[] ex)
        where T : IEnumerator<int>
    {
        using var enumerator = (T)Activator.CreateInstance(
            typeof(T),
            s1.Cast<int>().GetEnumerator(),
            s2.Cast<int>().GetEnumerator()
        )!;

        Assert.That(EnumerateOnce(enumerator), Is.EqualTo(ex));
    }

    private static IEnumerable<int> EnumerateOnce(IEnumerator<int> enumerator)
    {
        while (enumerator.MoveNext())
            yield return enumerator.Current;
    }

    private const long FromBlock = 0;
    private const long ToBlock = 99;

    private static readonly Ranges LogIndexRanges = GenerateLogIndexRanges();

    private static Ranges GenerateLogIndexRanges()
    {
        var random = new Random(42);

        var addressRanges = new Dictionary<Address, List<int>>();
        foreach (Address address in new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE })
        {
            var range = Enumerable.Range((int)FromBlock, (int)(ToBlock + 1)).Where(_ => random.NextDouble() < 0.3).ToList();
            addressRanges.Add(address, range);
        }

        Dictionary<Hash256, List<int>>[] topicRanges = Enumerable
            .Range(0, LogIndexStorage.MaxTopics)
            .Select(_ => new Dictionary<Hash256, List<int>>()).ToArray();

        foreach (Dictionary<Hash256, List<int>> ranges in topicRanges)
        {
            foreach (Hash256 topic in new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD, TestItem.KeccakE })
            {
                var range = Enumerable.Range((int)FromBlock, (int)(ToBlock + 1)).Where(_ => random.NextDouble() < 0.2).ToList();
                ranges.Add(topic, range);
            }
        }

        return new(addressRanges, topicRanges);
    }

    private static FilterBuilder BuildFilter() => FilterBuilder.New()
        .FromBlock(FromBlock)
        .ToBlock(ToBlock);
}
