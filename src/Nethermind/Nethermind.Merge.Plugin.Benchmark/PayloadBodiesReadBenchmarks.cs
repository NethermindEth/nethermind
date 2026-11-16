// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Testably.Abstractions;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>Reads payload bodies by range and by hash from a chain stored in RocksDB.</summary>
/// <remarks>The block cache is small and the requests are far below the head, so most reads go past the caches.</remarks>
[MemoryDiagnoser]
public class PayloadBodiesReadBenchmarks
{
    private const int ChainLength = 8192;
    private const int TransactionsPerBlock = 4;
    private const int TransactionDataLength = 1024;
    private const ulong RangeStart = 1024;
    private const ulong RangeCount = 1024;
    private const int HashCount = 32;

    private TempPath _directory = null!;
    private HyperClockCacheWrapper _blockCache = null!;
    private readonly List<IDb> _dbs = [];
    private IContainer _container = null!;
    private IGetPayloadBodiesByRangeV1Handler _rangeHandler = null!;
    private GetPayloadBodiesByHashV1Handler _hashHandler = null!;
    private Hash256[] _hashes = null!;
    private readonly PipeWriter _writer = PipeWriter.Create(Stream.Null);

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _blockCache = new HyperClockCacheWrapper(1_000_000);
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            RocksDbFactory factory = new(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                _blockCache, new RealFileSystem(), LimboLogs.Instance, _directory.Path);
            foreach (string name in (string[])[DbNames.Blocks, DbNames.Headers, DbNames.BlockNumbers, DbNames.BlockInfos])
            {
                _dbs.Add(factory.CreateDb(new DbSettings(name, name)));
            }
        }

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddKeyedSingleton(DbNames.Blocks, _dbs[0])
            .AddKeyedSingleton(DbNames.Headers, _dbs[1])
            .AddKeyedSingleton(DbNames.BlockNumbers, _dbs[2])
            .AddKeyedSingleton(DbNames.BlockInfos, _dbs[3])
            .AddSingleton<IGetPayloadBodiesByRangeV1Handler, GetPayloadBodiesByRangeV1Handler>()
            .AddSingleton<GetPayloadBodiesByHashV1Handler>()
            .Build();

        IBlockTree blockTree = _container.Resolve<IBlockTree>();
        Block[] chain = BuildChain(blockTree);
        foreach (IDb db in _dbs) db.Flush();

        Random random = new(42);
        _hashes = new Hash256[HashCount];
        for (int i = 0; i < HashCount; i++) _hashes[i] = chain[random.Next(1, ChainLength - (int)RangeCount)].Hash!;

        _rangeHandler = _container.Resolve<IGetPayloadBodiesByRangeV1Handler>();
        _hashHandler = _container.Resolve<GetPayloadBodiesByHashV1Handler>();
        Verify(chain);
    }

    private static Block[] BuildChain(IBlockTree blockTree)
    {
        Random random = new(1);
        Block[] chain = new Block[ChainLength + 1];
        chain[0] = Build.A.Block.Genesis.TestObject;
        blockTree.SuggestBlock(chain[0]);
        blockTree.TryUpdateMainChain(chain[0].Header, true, preloadedBlocks: [chain[0]]);
        for (int number = 1; number <= ChainLength; number++)
        {
            Transaction[] transactions = new Transaction[TransactionsPerBlock];
            for (int i = 0; i < transactions.Length; i++)
            {
                byte[] data = new byte[TransactionDataLength];
                random.NextBytes(data);
                transactions[i] = Build.A.Transaction.WithNonce((ulong)i).WithData(data).SignedAndResolved().TestObject;
            }

            chain[number] = Build.A.Block.WithParent(chain[number - 1]).WithTransactions(transactions).TestObject;
            blockTree.SuggestBlock(chain[number]);
            blockTree.TryUpdateMainChain(chain[number].Header, true, preloadedBlocks: [chain[number]]);
        }

        return chain;
    }

    private void Verify(Block[] chain)
    {
        IReadOnlyList<ExecutionPayloadBodyV1Result?> range = _rangeHandler.Handle(RangeStart, RangeCount).Result.Data;
        if (range.Count != (int)RangeCount) throw new InvalidOperationException("Wrong range result count.");
        for (int i = 0; i < range.Count; i++)
        {
            if (range[i]?.Transactions.Length != chain[(int)RangeStart + i].Transactions.Length)
                throw new InvalidOperationException($"Wrong body at range position {i}.");
        }

        IReadOnlyList<ExecutionPayloadBodyV1Result?> byHash = _hashHandler.Handle(_hashes).Data;
        if (byHash.Count != HashCount) throw new InvalidOperationException("Wrong by-hash result count.");
        for (int i = 0; i < byHash.Count; i++)
        {
            if (byHash[i]?.Transactions.Length != TransactionsPerBlock)
                throw new InvalidOperationException($"Wrong body at hash position {i}.");
        }
    }

    /// <summary>Reads the bodies, writes the response and disposes it, as the JSON-RPC response does.</summary>
    [Benchmark]
    public async Task<int> ByRange() => await Write((await _rangeHandler.Handle(RangeStart, RangeCount)).Data);

    /// <inheritdoc cref="ByRange"/>
    [Benchmark]
    public Task<int> ByHash() => Write(_hashHandler.Handle(_hashes).Data);

    private async Task<int> Write(IReadOnlyList<ExecutionPayloadBodyV1Result?> response)
    {
        await ((IStreamableResult)response).WriteToAsync(_writer, CancellationToken.None);
        await _writer.FlushAsync();
        response.TryDispose();
        return response.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        foreach (IDb db in _dbs) db.Dispose();
        _blockCache.Dispose();
        _directory.Dispose();
    }
}
