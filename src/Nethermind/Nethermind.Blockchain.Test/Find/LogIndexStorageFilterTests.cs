// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Find;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Find;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class LogIndexStorageFilterTests
{
    private record Ranges(Dictionary<Address, List<LongLogPosition>> Address, Dictionary<Hash256, List<LongLogPosition>>[] Topic)
    {
        public List<LongLogPosition> this[Address address] => Address[address];
        public List<LongLogPosition> this[int topicIndex, Hash256 hash] => Topic[topicIndex][hash];
    }

    private ILogIndexStorage _logIndexStorage = null!;

    [SetUp]
    public void SetUp()
    {
        _logIndexStorage = Substitute.For<ILogIndexStorage>();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _logIndexStorage.DisposeAsync();
    }

    public static IEnumerable LogIndexTestsData
    {
        get
        {
            yield return new TestCaseData(
                "AddressA", // name

                BuildFilter() // filter
                    .WithAddress(TestItem.AddressA)
                    .Build(),

                LogIndexRanges[TestItem.AddressA] // expected range
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "AddressA or AddressA",

                BuildFilter()
                    .WithAddresses(TestItem.AddressA, TestItem.AddressA)
                    .Build(),

                LogIndexRanges[TestItem.AddressA]
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "AddressA or AddressB",

                BuildFilter()
                    .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                    .Build(),

                LogIndexRanges[TestItem.AddressA].Union(LogIndexRanges[TestItem.AddressB])
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Specific(TestItem.KeccakA)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA or TopicA",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakA)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA or TopicB",

                BuildFilter()
                    .WithTopicExpressions(
                        TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA].Union(LogIndexRanges[0, TestItem.KeccakB])
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
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
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
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
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
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
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
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
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
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
                    .Select(p => p.BlockNumber).Distinct().Order().ToList()
            );
        }
    }

    [TestCaseSource(nameof(LogIndexTestsData))]
    public void calculates_correct_range_for_filter(string name, LogFilter filter, List<int> expected)
    {
        Assert.That(expected,
            Has.Count.InRange(from: 1, to: ToBlock - FromBlock - 1),
            "Unreliable test: none or all blocks are selected."
        );

        MockLogIndex();

        IList<int> blockNums = _logIndexStorage.GetBlockNumbersFor(filter, FromBlock, ToBlock, CancellationToken.None);

        Assert.That(blockNums, Is.EquivalentTo(expected));
    }

    private const long FromBlock = 0;
    private const long ToBlock = 99;
    private const int PositionsPerBlock = 5;
    private const double AddressLogFrequency = 0.06 * 2.3;
    private const double TopicLogFrequency = 0.04 * 2.3;

    private static readonly Ranges LogIndexRanges = GenerateLogIndexRanges();

    private static Ranges GenerateLogIndexRanges()
    {
        var random = new Random(42);

        var addressRanges = new Dictionary<Address, List<LongLogPosition>>();
        foreach (Address address in new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE })
        {
            var range = Enumerable.Range((int)FromBlock, (int)(ToBlock + 1))
                .SelectMany(block => Enumerable.Range(0, PositionsPerBlock).Select(logIndex => new LongLogPosition(block, logIndex)))
                .Where(_ => random.NextDouble() < AddressLogFrequency)
                .ToList();

            addressRanges.Add(address, range);
        }

        Dictionary<Hash256, List<LongLogPosition>>[] topicRanges = Enumerable
            .Range(0, LogIndexStorage.MaxTopics)
            .Select(_ => new Dictionary<Hash256, List<LongLogPosition>>()).ToArray();

        foreach (Dictionary<Hash256, List<LongLogPosition>> ranges in topicRanges)
        {
            foreach (Hash256 topic in new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD, TestItem.KeccakE })
            {
                var range = Enumerable.Range((int)FromBlock, (int)(ToBlock + 1))
                    .SelectMany(block => Enumerable.Range(0, PositionsPerBlock).Select(logIndex => new LongLogPosition(block, logIndex)))
                    .Where(_ => random.NextDouble() < TopicLogFrequency)
                    .ToList();
                ranges.Add(topic, range);
            }
        }

        return new(addressRanges, topicRanges);
    }

    private void MockLogIndex()
    {
        foreach ((Address address, List<LongLogPosition> range) in LogIndexRanges.Address)
        {
            _logIndexStorage
                .GetLogPositions(address, Arg.Any<int>(), Arg.Any<int>())
                .Returns(info => range.SkipWhile(p => p.BlockNumber < info.ArgAt<int>(1)).TakeWhile(p => p.BlockNumber <= info.ArgAt<int>(2)).ToList());
        }

        for (var i = 0; i < LogIndexRanges.Topic.Length; i++)
        {
            foreach ((Hash256 topic, List<LongLogPosition> range) in LogIndexRanges.Topic[i])
            {
                _logIndexStorage
                    .GetLogPositions(Arg.Is(i), topic, Arg.Any<int>(), Arg.Any<int>())
                    .Returns(info => range.SkipWhile(p => p.BlockNumber < info.ArgAt<int>(2)).TakeWhile(p => p.BlockNumber <= info.ArgAt<int>(3)).ToList());
            }
        }
    }

    private static FilterBuilder BuildFilter() => FilterBuilder.New()
        .FromBlock(FromBlock)
        .ToBlock(ToBlock);
}
