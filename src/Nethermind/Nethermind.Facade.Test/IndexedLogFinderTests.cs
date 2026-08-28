// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test;

public class IndexedLogFinderTests
{
    private const ulong OldestStored = 50;
    private const int From = 10;
    private const int To = 200;

    private IBlockFinder _blockFinder = null!;
    private IReceiptStorage _receiptStorage = null!;
    private ILogIndexStorage _logIndexStorage = null!;
    private BlockHeader _fromHeader = null!;
    private BlockHeader _toHeader = null!;

    [SetUp]
    public void SetUp()
    {
        _blockFinder = Substitute.For<IBlockFinder>();
        _blockFinder.GetLowestBlock().Returns(OldestStored);
        _receiptStorage = Substitute.For<IReceiptStorage>();
        _logIndexStorage = Substitute.For<ILogIndexStorage>();
        _logIndexStorage.Enabled.Returns(true);
        _logIndexStorage.MinBlockNumber.Returns(0);
        _logIndexStorage.MaxBlockNumber.Returns(To);
        _fromHeader = Build.A.BlockHeader.WithNumber(From).WithReceiptsRoot(TestItem.KeccakA).TestObject;
        _toHeader = Build.A.BlockHeader.WithNumber(To).WithReceiptsRoot(TestItem.KeccakB).TestObject;
    }

    [TearDown]
    public async Task TearDownAsync() => await _logIndexStorage.DisposeAsync();

    private IndexedLogFinder GetFinder(IPrunedLogsRetention? prunedLogsRetention) => new(
        _blockFinder,
        Substitute.For<IReceiptFinder>(),
        _receiptStorage,
        LimboLogs.Instance,
        Substitute.For<IReceiptsRecovery>(),
        _logIndexStorage,
        prunedLogsRetention: prunedLogsRetention);

    private static LogFilter CreateFilter() => new(
        0,
        new BlockParameter(From),
        new BlockParameter(To),
        new AddressFilter(TestItem.AddressA),
        new SequenceTopicsFilter());

    [Test]
    public void Should_ServeBelowTheOldestStoredBlockFromTheIndex_WhenTheRetentionCoversTheFilter()
    {
        IPrunedLogsRetention retention = Substitute.For<IPrunedLogsRetention>();
        retention.RetainsLogsFor(Arg.Any<IReadOnlyCollection<AddressAsKey>>(), Arg.Any<ulong>(), Arg.Any<ulong>()).Returns(true);

        FilterLog[] logs = GetFinder(retention).FindLogs(CreateFilter(), _fromHeader, _toHeader, CancellationToken.None).ToArray();

        Assert.That(logs, Is.Empty);
        _logIndexStorage.Received().GetEnumerator(TestItem.AddressA, From, To);
    }

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_WhenTheRetentionDoesNotCoverTheFilter()
    {
        IPrunedLogsRetention retention = Substitute.For<IPrunedLogsRetention>();
        retention.RetainsLogsFor(Arg.Any<IReadOnlyCollection<AddressAsKey>>(), Arg.Any<ulong>(), Arg.Any<ulong>()).Returns(false);

        Assert.Throws<ResourceNotFoundException>(() =>
            GetFinder(retention).FindLogs(CreateFilter(), _fromHeader, _toHeader, CancellationToken.None).ToArray());
    }

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_WhenNoRetentionIsConfigured() =>
        Assert.Throws<ResourceNotFoundException>(() =>
            GetFinder(prunedLogsRetention: null).FindLogs(CreateFilter(), _fromHeader, _toHeader, CancellationToken.None).ToArray());

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_EvenWhenBothEndpointsCarryNoReceipts()
    {
        BlockHeader emptyFrom = Build.A.BlockHeader.WithNumber(From).TestObject;
        BlockHeader emptyTo = Build.A.BlockHeader.WithNumber(To).TestObject;

        Assert.That(emptyFrom.ReceiptsRoot, Is.EqualTo(Keccak.EmptyTreeHash),
            "the scenario needs endpoints the endpoint probe cannot see, so their receipt roots must be empty");

        Assert.Throws<ResourceNotFoundException>(() =>
            GetFinder(prunedLogsRetention: null).FindLogs(CreateFilter(), emptyFrom, emptyTo, CancellationToken.None).ToArray());
    }

    [Test]
    public void Should_UseTheFullIndexRange_WhenTheQueryDoesNotReachBelowTheOldestStoredBlock()
    {
        _blockFinder.GetLowestBlock().Returns(1UL);

        FilterLog[] logs = GetFinder(prunedLogsRetention: null).FindLogs(CreateFilter(), _fromHeader, _toHeader, CancellationToken.None).ToArray();

        Assert.That(logs, Is.Empty);
        _logIndexStorage.Received().GetEnumerator(TestItem.AddressA, From, To);
    }

    [Test]
    public void Should_AnswerAFromGenesisQueryUnchanged_OnANodeThatNeverPrunedHistory()
    {
        _blockFinder.GetLowestBlock().Returns(1UL);
        BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).TestObject;
        _blockFinder.FindHeader(0UL).Returns(genesis);
        LogFilter filter = new(
            0,
            new BlockParameter(0UL),
            new BlockParameter(To),
            new AddressFilter(TestItem.AddressA),
            new SequenceTopicsFilter());

        FilterLog[] logs = GetFinder(prunedLogsRetention: null).FindLogs(filter, genesis, _toHeader, CancellationToken.None).ToArray();

        Assert.That(logs, Is.Empty);
        _logIndexStorage.Received().GetEnumerator(TestItem.AddressA, 1, To);
    }
}
