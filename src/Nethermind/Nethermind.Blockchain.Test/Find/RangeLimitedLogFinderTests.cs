// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Find;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Find;

[Parallelizable(ParallelScope.All)]
public class RangeLimitedLogFinderTests
{
    private const int FromBlockNumber = 100;
    private const int ToBlockNumber = 104; // a five block range

    [TestCase(2, true, TestName = "range 5 > limit 2 -> rejected")]
    [TestCase(5, false, TestName = "range 5 <= limit 5 -> allowed")]
    [TestCase(0, false, TestName = "limit disabled -> allowed")]
    public void Enforces_max_block_depth(int maxBlockDepth, bool shouldThrow) =>
        AssertFindLogs(CreateLogFinder(out ILogFinder inner, maxBlockDepth: maxBlockDepth), inner, Filter(), shouldThrow);

    [TestCase(FromBlockNumber, ToBlockNumber, false, TestName = "index covers the range -> allowed")]
    [TestCase(FromBlockNumber, ToBlockNumber - 2, false, TestName = "index lags the head by 2 -> 2 blocks read, allowed")]
    [TestCase(FromBlockNumber + 3, ToBlockNumber, true, TestName = "index starts 3 blocks in -> 3 blocks read, rejected")]
    [TestCase(ToBlockNumber + 1, ToBlockNumber + 9, true, TestName = "index is past the range -> whole range read, rejected")]
    public void Counts_only_the_blocks_the_log_index_cannot_answer(int indexFrom, int indexTo, bool shouldThrow) =>
        AssertFindLogs(
            CreateLogFinder(out ILogFinder inner, indexFrom: indexFrom, indexTo: indexTo),
            inner, IndexableFilter(), shouldThrow);

    [Test]
    public void Enforces_max_block_depth_on_a_caller_opting_out_of_the_log_index()
    {
        LogFilter filter = IndexableFilter();
        filter.UseIndex = false;

        AssertFindLogs(CreateLogFinder(out ILogFinder inner, indexFrom: 0, indexTo: ToBlockNumber), inner, filter, shouldThrow: true);
    }

    [Test]
    public void Enforces_max_block_depth_on_a_filter_the_log_index_cannot_serve() =>
        AssertFindLogs(
            CreateLogFinder(out ILogFinder inner, indexFrom: 0, indexTo: ToBlockNumber),
            inner, Filter(), shouldThrow: true); // no address and no topic, so IndexedLogFinder reads sequentially

    [Test]
    public void Rejects_before_the_result_is_enumerated()
    {
        RangeLimitedLogFinder logFinder = CreateLogFinder(out ILogFinder _);

        Assert.That(() => logFinder.FindLogs(Filter(), Header(FromBlockNumber), Header(ToBlockNumber)),
            Throws.TypeOf<ArgumentException>().With.Message.Contains(nameof(IReceiptConfig.MaxBlockDepth)));
    }

    [Test]
    public void Resolves_the_range_of_a_filter_given_without_headers()
    {
        RangeLimitedLogFinder logFinder = CreateLogFinder(out ILogFinder inner);

        Assert.That(() => logFinder.FindLogs(Filter()).ToArray(),
            Throws.TypeOf<ArgumentException>().With.Message.Contains(nameof(IReceiptConfig.MaxBlockDepth)));
        inner.DidNotReceiveWithAnyArgs().FindLogs(default!, default!, default!);
    }

    private static void AssertFindLogs(RangeLimitedLogFinder logFinder, ILogFinder inner, LogFilter filter, bool shouldThrow)
    {
        if (shouldThrow)
        {
            Assert.That(() => logFinder.FindLogs(filter, Header(FromBlockNumber), Header(ToBlockNumber)).ToArray(),
                Throws.TypeOf<ArgumentException>().With.Message.Contains(nameof(IReceiptConfig.MaxBlockDepth)));
            inner.DidNotReceiveWithAnyArgs().FindLogs(default!, default!, default!);
        }
        else
        {
            Assert.That(() => logFinder.FindLogs(filter, Header(FromBlockNumber), Header(ToBlockNumber)).ToArray(), Throws.Nothing);
            inner.ReceivedWithAnyArgs(1).FindLogs(default!, default!, default!);
        }
    }

    private static RangeLimitedLogFinder CreateLogFinder(out ILogFinder inner, int maxBlockDepth = 2, int? indexFrom = null, int? indexTo = null)
    {
        inner = Substitute.For<ILogFinder>();
        inner.FindLogs(default!, default!, default!).ReturnsForAnyArgs([]);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindHeader(Arg.Any<BlockParameter>(), Arg.Any<bool>())
            .Returns(callInfo => Header((int)callInfo.Arg<BlockParameter>().BlockNumber!.Value));

        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.MinBlockNumber.Returns(indexFrom);
        logIndexStorage.MaxBlockNumber.Returns(indexTo);

        return new RangeLimitedLogFinder(inner, blockFinder, new ReceiptConfig { MaxBlockDepth = maxBlockDepth }, logIndexStorage);
    }

    private static LogFilter Filter() => FilterBuilder.New().FromBlock(FromBlockNumber).ToBlock(ToBlockNumber).Build();

    private static LogFilter IndexableFilter() => FilterBuilder.New()
        .FromBlock(FromBlockNumber).ToBlock(ToBlockNumber).WithAddress(TestItem.AddressA).Build();

    private static BlockHeader Header(int number) => Build.A.BlockHeader.WithNumber(number).TestObject;
}
