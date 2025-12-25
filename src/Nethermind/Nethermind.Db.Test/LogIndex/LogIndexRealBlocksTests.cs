// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db.LogIndex;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Db.Test.LogIndex;

[Parallelizable(ParallelScope.All)]
public class LogIndexRealBlocksTests
{
    // ReSharper disable once ClassNeverInstantiated.Local
    private class LogJson
    {
        public required Address Address { get; init; }
        public required long BlockNumber { get; init; }
        public required byte[] Data { get; init; }
        public required Hash256[] Topics { get; init; }
        public required Hash256 TransactionHash { get; init; }
    }

    private static readonly IJsonRpcClient Client = new BasicJsonRpcClient(
        new("https://rpc.gnosis.gateway.fm"),
        new EthereumJsonSerializer(),
        new TestLogManager()
    );

    [OneTimeTearDown]
    public void GlobalTearDown()
    {
        (Client as IDisposable)?.Dispose();
    }

    private static async Task<BlockReceipts[]> GetBlockReceiptsAsync(int fromBlock, int toBlock, int perRequest)
    {
        List<LogJson> rpcLogs = new();

        for (int reqFrom = fromBlock; reqFrom <= toBlock - perRequest + 1; reqFrom += perRequest)
        {
            var reqTo = Math.Min(reqFrom + perRequest - 1, toBlock);
            rpcLogs.AddRange(await Client.Post<LogJson[]>(
                "eth_getLogs", new { fromBlock = new BlockParameter(reqFrom), toBlock = new BlockParameter(reqTo) }
            ));
        }

        var logsMap = Enumerable
            .Range(fromBlock, toBlock - fromBlock + 1)
            .ToDictionary(static blockNum => blockNum, static _ => new Dictionary<Hash256, List<LogEntry>>());

        foreach (LogJson log in rpcLogs)
        {
            Dictionary<Hash256, List<LogEntry>> logsByTxHash = logsMap[(int)log.BlockNumber];
            List<LogEntry> logs = logsByTxHash.GetOrAdd(log.TransactionHash, static _ => new());
            logs.Add(new LogEntry(log.Address, log.Data, log.Topics));
        }

        return logsMap.Select(x => new BlockReceipts
        {
            BlockNumber = x.Key,
            Receipts = x.Value.Select(static x => new TxReceipt { TxHash = x.Key, Logs = x.Value.ToArray() }).ToArray()
        }).ToArray();
    }

    // TODO: move to benchmarks project?
    [TestCase(29204851, 29205100, 50)] // https://gnosisscan.io/txs?a=0xc77a4cb9a4c2edd368173287729a01dc31db2f00&ps=100
    [TestCase(34146001, 34146300, 10)] // https://gnosisscan.io/txs?a=0x191eafa52eb2c4cd414e1b3fca132cee0938b920&ps=100
    public async Task TimeAggregate(int fromBlock, int toBlock, int perRequest)
    {
        await using var storage = new LogIndexStorage(new MemDbFactory(), new TestLogManager(), new LogIndexConfig());
        BlockReceipts[]? batch = await GetBlockReceiptsAsync(fromBlock, toBlock, perRequest);

        var timestamp = Stopwatch.GetTimestamp();
        LogIndexAggregate aggregate = storage.Aggregate(batch, false, null);
        await TestContext.Out.WriteLineAsync($"{nameof(LogIndexStorage.Aggregate)} in {Stopwatch.GetElapsedTime(timestamp)}");
    }
}
