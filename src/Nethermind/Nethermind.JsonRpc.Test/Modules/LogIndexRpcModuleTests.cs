// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Db.LogIndex;
using Nethermind.Facade;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Find;
using Nethermind.JsonRpc.Modules.LogIndex;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class LogIndexRpcModuleTests
{
    private const long HeadNumber = 10;
    private const int MaxResults = 3;

    [TestCase("""{"fromBlock":"latest","toBlock":"latest"}""")]
    [TestCase("""{"fromBlock":"0x1","toBlock":"0x5","topics":[null]}""")]
    [TestCase("""{"fromBlock":"0x1","toBlock":"0x5","topics":[[],null]}""")]
    [TestCase("""{"fromBlock":"0x1","toBlock":"0x5","address":[]}""")]
    public async Task logIndex_blockNumbers_without_address_or_topics_returns_invalid_params(string filter)
    {
        string serialized = await RpcTest.TestSerializedRequest(CreateModule(EnabledStorage()), "logIndex_blockNumbers", filter);

        Assert.That(serialized, Does.Contain($"\"code\":{ErrorCodes.InvalidParams}"));
    }

    [Test]
    public async Task logIndex_blockNumbers_with_address_returns_indexed_blocks()
    {
        ILogIndexStorage storage = EnabledStorage();
        storage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>())
            .Returns(_ => ((IEnumerable<int>)[2, 4]).GetEnumerator());

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0x5","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":["0x2","0x4"],"id":67}"""));
    }

    [Test]
    public async Task logIndex_blockNumbers_with_topic_after_wildcard_returns_indexed_blocks()
    {
        ILogIndexStorage storage = EnabledStorage();
        storage.GetEnumerator(1, TestItem.KeccakA, Arg.Any<int>(), Arg.Any<int>())
            .Returns(_ => ((IEnumerable<int>)[3]).GetEnumerator());

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0x5","address":[],"topics":[null,"{{TestItem.KeccakA}}",[]]}""");

        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":["0x3"],"id":67}"""));
    }

    [Test]
    public async Task logIndex_blockNumbers_with_failing_storage_returns_error()
    {
        ILogIndexStorage storage = EnabledStorage();
        storage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>())
            .Returns(_ => throw new InvalidOperationException("Storage failure"));

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0x5","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Does.Contain($"\"code\":{ErrorCodes.InternalError}"));
    }

    [Test]
    public async Task logIndex_blockNumbers_with_disabled_index_returns_error()
    {
        string serialized = await RpcTest.TestSerializedRequest(CreateModule(new DisabledLogIndexStorage()), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0x5","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Does.Contain($"\"code\":{ErrorCodes.ResourceUnavailable}"));
    }

    [Test]
    public async Task logIndex_blockNumbers_at_result_limit_returns_indexed_blocks()
    {
        ILogIndexStorage storage = EnabledStorage();
        storage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>())
            .Returns(_ => ((IEnumerable<int>)[2, 4, 6]).GetEnumerator());

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0xa","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":["0x2","0x4","0x6"],"id":67}"""));
    }

    [Test]
    public async Task logIndex_blockNumbers_over_result_limit_returns_error_without_enumerating_all_blocks()
    {
        int enumerated = 0;
        IEnumerator<int> EveryBlock()
        {
            for (int block = 1; block <= HeadNumber; block++)
            {
                enumerated++;
                yield return block;
            }
        }

        ILogIndexStorage storage = EnabledStorage();
        storage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>()).Returns(_ => EveryBlock());

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0xa","address":"{{TestItem.AddressA}}"}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialized, Does.Contain($"\"code\":{ErrorCodes.LimitExceeded}"));
            Assert.That(enumerated, Is.EqualTo(MaxResults + 1));
        }
    }

    [Test]
    public async Task logIndex_blockNumbers_with_lifted_result_limit_returns_all_indexed_blocks()
    {
        ILogIndexStorage storage = EnabledStorage();
        storage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>())
            .Returns(_ => ((IEnumerable<int>)[2, 4, 6, 8, 10]).GetEnumerator());

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage, maxResults: 0), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0xa","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":["0x2","0x4","0x6","0x8","0xa"],"id":67}"""));
    }

    [Test]
    public async Task logIndex_blockNumbers_over_result_limit_disposes_storage_enumerator()
    {
        bool disposed = false;
        IEnumerator<int> EveryBlock()
        {
            try
            {
                for (int block = 1; block <= HeadNumber; block++)
                    yield return block;
            }
            finally
            {
                disposed = true;
            }
        }

        ILogIndexStorage storage = EnabledStorage();
        storage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>()).Returns(_ => EveryBlock());

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0xa","address":"{{TestItem.AddressA}}"}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialized, Does.Contain($"\"code\":{ErrorCodes.LimitExceeded}"));
            Assert.That(disposed, Is.True);
        }
    }

    [Test]
    public async Task logIndex_blockNumbers_without_max_indexed_block_returns_error()
    {
        // The state after a reorg the index could not revert: postings remain but MaxBlockNumber is null
        ILogIndexStorage storage = EnabledStorage();
        storage.MaxBlockNumber.Returns((int?)null);
        storage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>())
            .Returns(_ => ((IEnumerable<int>)[2, 4]).GetEnumerator());

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0x5","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Does.Contain($"\"code\":{ErrorCodes.ResourceUnavailable}"));
    }

    [TestCase(3, 5, "0x1", "0xa", """["0x4"]""", TestName = "Range wider than index on both sides")]
    [TestCase(0, 5, "0x7", "0xa", "[]", TestName = "Range entirely above index")]
    [TestCase(6, 10, "0x1", "0x5", "[]", TestName = "Range entirely below index")]
    public async Task logIndex_blockNumbers_returns_only_blocks_within_indexed_range(int indexFrom, int indexTo, string fromBlock, string toBlock, string expected)
    {
        // Postings outside the indexed range, such as ones left behind by a reorg, must not be returned
        ILogIndexStorage storage = EnabledStorage();
        storage.MinBlockNumber.Returns((int?)indexFrom);
        storage.MaxBlockNumber.Returns((int?)indexTo);
        ReturnPostings(storage, 2, 4, 8);

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"{{fromBlock}}","toBlock":"{{toBlock}}","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","result":{{expected}},"id":67}"""));
    }

    [Test]
    public async Task logIndex_blockNumbers_applies_result_limit_after_clamping_to_indexed_range()
    {
        ILogIndexStorage storage = EnabledStorage();
        storage.MaxBlockNumber.Returns((int?)6);
        ReturnPostings(storage, 2, 4, 6, 8, 10);

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0xa","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":["0x2","0x4","0x6"],"id":67}"""));
    }

    [Test]
    public async Task logIndex_blockNumbers_without_min_indexed_block_returns_error()
    {
        ILogIndexStorage storage = EnabledStorage();
        storage.MinBlockNumber.Returns((int?)null);
        ReturnPostings(storage, 2, 4);

        string serialized = await RpcTest.TestSerializedRequest(CreateModule(storage), "logIndex_blockNumbers",
            $$"""{"fromBlock":"0x1","toBlock":"0x5","address":"{{TestItem.AddressA}}"}""");

        Assert.That(serialized, Does.Contain($"\"code\":{ErrorCodes.ResourceUnavailable}"));
    }

    // Mimics storage: yields the postings within the requested range, whatever the indexed range is
    private static void ReturnPostings(ILogIndexStorage storage, params int[] blocks) =>
        storage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>())
            .Returns(info => blocks.Where(b => b >= info.ArgAt<int>(1) && b <= info.ArgAt<int>(2)).GetEnumerator());

    private static ILogIndexStorage EnabledStorage()
    {
        ILogIndexStorage storage = Substitute.For<ILogIndexStorage>();
        storage.Enabled.Returns(true);
        storage.MinBlockNumber.Returns((int?)0);
        storage.MaxBlockNumber.Returns((int?)HeadNumber);
        return storage;
    }

    private static ILogIndexRpcModule CreateModule(ILogIndexStorage storage, int maxResults = MaxResults)
    {
        FilterStore filterStore = new(Substitute.For<ITimerFactory>());
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetFilter(Arg.Any<BlockParameter>(), Arg.Any<BlockParameter>(), Arg.Any<HashSet<AddressAsKey>?>(), Arg.Any<IEnumerable<Hash256[]?>?>())
            .Returns(info => filterStore.CreateLogFilter(info.ArgAt<BlockParameter>(0), info.ArgAt<BlockParameter>(1),
                info.ArgAt<HashSet<AddressAsKey>?>(2), info.ArgAt<IEnumerable<Hash256[]?>?>(3), setId: false));

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(Build.A.Block.WithNumber(HeadNumber).TestObject);

        return new LogIndexRpcModule(storage, Substitute.For<ILogIndexBuilder>(), blockFinder, bridge, new JsonRpcConfig { MaxLogsPerResponse = maxResults });
    }
}
