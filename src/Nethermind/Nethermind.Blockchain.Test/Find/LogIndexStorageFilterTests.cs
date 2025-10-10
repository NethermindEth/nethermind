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
using Nethermind.Db;
using Nethermind.Facade.Find;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Find;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class LogIndexStorageFilterTests
{
    private record Ranges(Dictionary<Address, List<int>> Address, Dictionary<Hash256, List<int>>[] Topic)
    {
        public List<int> this[Address address] => Address[address];
        public List<int> this[int topicIndex, Hash256 hash] => Topic[topicIndex][hash];
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
                "AddressA",

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
                    .WithAddress(TestItem.AddressA)
                    .Build(),

                LogIndexRanges[TestItem.AddressA]
            );

            yield return new TestCaseData(
                "AddressA or AddressA",

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
                    .WithAddresses(TestItem.AddressA, TestItem.AddressA)
                    .Build(),

                LogIndexRanges[TestItem.AddressA]
            );

            yield return new TestCaseData(
                "AddressA or AddressB",

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
                    .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                    .Build(),

                LogIndexRanges[TestItem.AddressA].Union(LogIndexRanges[TestItem.AddressB])
                    .Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA",

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
                    .WithTopicExpressions(
                        TestTopicExpressions.Specific(TestItem.KeccakA)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
            );

            yield return new TestCaseData(
                "TopicA or TopicA",

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
                    .WithTopicExpressions(
                        TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakA)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
            );

            yield return new TestCaseData(
                "TopicA or TopicB",

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
                    .WithTopicExpressions(
                        TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA].Union(LogIndexRanges[0, TestItem.KeccakB])
                    .Distinct().Order().ToList()
            );

            yield return new TestCaseData(
                "TopicA, TopicB",

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
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

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
                    .WithTopicExpressions(
                        TestTopicExpressions.Specific(TestItem.KeccakA),
                        TestTopicExpressions.Any,
                        TestTopicExpressions.Specific(TestItem.KeccakA)
                    ).Build(),

                LogIndexRanges[0, TestItem.KeccakA]
                    .Intersect(LogIndexRanges[2, TestItem.KeccakA])
                    .Distinct().Order().ToList()
            );

            // TODO: cases with the same topic on different positions

            yield return new TestCaseData(
                "TopicA, -, TopicB or TopicC",

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
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

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
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

                FilterBuilder.New()
                    .FromBlock(LogIndexFrom).ToBlock(LogIndexTo)
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

    [TestCaseSource(nameof(LogIndexTestsData))]
    public void Filter_Test(string name, LogFilter filter, List<int> expected)
    {
        Assert.That(expected, Is.Not.Empty, "Unreliable test: no block numbers are selected.");
        Assert.That(expected, Has.Count.LessThan(LogIndexTo - LogIndexFrom + 1), "Unreliable test: all block numbers are selected.");

        MockLogIndex();

        List<int> blockNums = _logIndexStorage.GetBlockNumbersFor(filter, LogIndexFrom, LogIndexTo, CancellationToken.None);

        Assert.That(blockNums, Is.EquivalentTo(expected));
    }

    private const long LogIndexFrom = 0;
    private const long LogIndexTo = 99;

    private static readonly Ranges LogIndexRanges = GenerateLogIndexRanges();

    private static Ranges GenerateLogIndexRanges()
    {
        var random = new Random(42);

        var addressRanges = new Dictionary<Address, List<int>>();
        foreach (Address address in new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE })
        {
            var range = Enumerable.Range((int)LogIndexFrom, (int)(LogIndexTo + 1)).Where(_ => random.NextDouble() < 0.3).ToList();
            addressRanges.Add(address, range);
        }

        Dictionary<Hash256, List<int>>[] topicRanges = Enumerable
            .Range(0, LogIndexStorage.MaxTopics)
            .Select(_ => new Dictionary<Hash256, List<int>>()).ToArray();

        foreach (Dictionary<Hash256, List<int>> ranges in topicRanges)
        {
            foreach (Hash256 topic in new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD, TestItem.KeccakE })
            {
                var range = Enumerable.Range((int)LogIndexFrom, (int)(LogIndexTo + 1)).Where(_ => random.NextDouble() < 0.2).ToList();
                ranges.Add(topic, range);
            }
        }

        return new(addressRanges, topicRanges);
    }

    private void MockLogIndex()
    {
        foreach ((Address address, List<int> range) in LogIndexRanges.Address)
        {
            _logIndexStorage
                .GetBlockNumbersFor(address, Arg.Any<int>(), Arg.Any<int>())
                .Returns(info => range.SkipWhile(x => x < info.ArgAt<int>(1)).TakeWhile(x => x <= info.ArgAt<int>(2)).ToList());
        }

        for (var i = 0; i < LogIndexRanges.Topic.Length; i++)
        {
            foreach ((Hash256 topic, List<int> range) in LogIndexRanges.Topic[i])
            {
                _logIndexStorage
                    .GetBlockNumbersFor(Arg.Is(i), topic, Arg.Any<int>(), Arg.Any<int>())
                    .Returns(info => range.SkipWhile(x => x < info.ArgAt<int>(2)).TakeWhile(x => x <= info.ArgAt<int>(3)).ToList());
            }
        }
    }
}
