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
using Nethermind.Blockchain.BlockAccessLists;
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
/// <remarks>The block cache is small and the requests are far below the head, so most reads go past the caches.
/// Each block has a block access list as large as its transaction data, which only the V2 methods read.</remarks>
[MemoryDiagnoser]
public class PayloadBodiesReadBenchmarks
{
    private const int MinChainLength = 2560;
    private const int ChainTransactionCount = 32 * 1024;
    private const int DistinctTransactionCount = 1024;
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
    private IGetPayloadBodiesByRangeV2Handler _rangeHandlerV2 = null!;
    private GetPayloadBodiesByHashV2Handler _hashHandlerV2 = null!;
    private Hash256[] _hashes = null!;
    private readonly PipeWriter _writer = PipeWriter.Create(Stream.Null);

    /// <summary>4 transactions make a small body; 128 make a body of about 130 KB, as on mainnet.</summary>
    /// <remarks>The block access list is then 4 KiB or 128 KiB. EIP-7928 measures about 92 KiB compressed on average at
    /// a 60M gas limit, and the stored list is not compressed.</remarks>
    [Params(4, 128)]
    public int TransactionsPerBlock { get; set; }

    private int ChainLength => Math.Max(MinChainLength, ChainTransactionCount / TransactionsPerBlock);

    private int BlockAccessListLength => TransactionsPerBlock * TransactionDataLength;

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _blockCache = new HyperClockCacheWrapper(1_000_000);
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            RocksDbFactory factory = new(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                _blockCache, new RealFileSystem(), LimboLogs.Instance, _directory.Path);
            foreach (string name in (string[])[DbNames.Blocks, DbNames.Headers, DbNames.BlockNumbers, DbNames.BlockInfos, DbNames.BlockAccessLists])
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
            .AddKeyedSingleton(DbNames.BlockAccessLists, _dbs[4])
            .AddSingleton<IGetPayloadBodiesByRangeV1Handler, GetPayloadBodiesByRangeV1Handler>()
            .AddSingleton<GetPayloadBodiesByHashV1Handler>()
            .AddSingleton<IGetPayloadBodiesByRangeV2Handler, GetPayloadBodiesByRangeV2Handler>()
            .AddSingleton<GetPayloadBodiesByHashV2Handler>()
            .Build();

        Block[] chain = BuildChain(_container.Resolve<IBlockTree>(), _container.Resolve<IBlockAccessListStore>());
        foreach (IDb db in _dbs) db.Flush();

        Random random = new(42);
        _hashes = new Hash256[HashCount];
        for (int i = 0; i < HashCount; i++) _hashes[i] = chain[random.Next(1, ChainLength - (int)RangeCount)].Hash!;

        _rangeHandler = _container.Resolve<IGetPayloadBodiesByRangeV1Handler>();
        _hashHandler = _container.Resolve<GetPayloadBodiesByHashV1Handler>();
        _rangeHandlerV2 = _container.Resolve<IGetPayloadBodiesByRangeV2Handler>();
        _hashHandlerV2 = _container.Resolve<GetPayloadBodiesByHashV2Handler>();
        Verify(chain);
    }

    private Block[] BuildChain(IBlockTree blockTree, IBlockAccessListStore balStore)
    {
        Random random = new(1);
        // Signing dominates the setup, so blocks share a set of distinct transactions.
        Transaction[] distinct = new Transaction[DistinctTransactionCount];
        for (int i = 0; i < distinct.Length; i++)
        {
            byte[] data = new byte[TransactionDataLength];
            random.NextBytes(data);
            distinct[i] = Build.A.Transaction.WithNonce((ulong)i).WithData(data).SignedAndResolved().TestObject;
        }

        // The response writes the access list as hex without decoding it, so random bytes will do.
        byte[] bal = new byte[BlockAccessListLength];
        Block[] chain = new Block[ChainLength + 1];
        chain[0] = Build.A.Block.Genesis.TestObject;
        blockTree.SuggestBlock(chain[0]);
        blockTree.TryUpdateMainChain(chain[0].Header, true, preloadedBlocks: [chain[0]]);
        for (int number = 1; number <= ChainLength; number++)
        {
            Transaction[] transactions = new Transaction[TransactionsPerBlock];
            int first = random.Next(distinct.Length);
            for (int i = 0; i < transactions.Length; i++) transactions[i] = distinct[(first + i) % distinct.Length];

            chain[number] = Build.A.Block.WithParent(chain[number - 1]).WithTransactions(transactions).TestObject;
            blockTree.SuggestBlock(chain[number]);
            blockTree.TryUpdateMainChain(chain[number].Header, true, preloadedBlocks: [chain[number]]);
            random.NextBytes(bal);
            balStore.Insert((ulong)number, chain[number].Hash!, bal);
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

        using IDisposable rangeV2 = (IDisposable)_rangeHandlerV2.Handle(RangeStart, RangeCount).Result.Data;
        VerifyBlockAccessLists((IReadOnlyList<ExecutionPayloadBodyV2Result?>)rangeV2, (int)RangeCount);
        using IDisposable byHashV2 = (IDisposable)_hashHandlerV2.Handle(_hashes).Data;
        VerifyBlockAccessLists((IReadOnlyList<ExecutionPayloadBodyV2Result?>)byHashV2, HashCount);
    }

    private void VerifyBlockAccessLists(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies, int count)
    {
        if (bodies.Count != count) throw new InvalidOperationException("Wrong V2 result count.");
        for (int i = 0; i < bodies.Count; i++)
        {
            if (bodies[i]?.BlockAccessList?.Length != BlockAccessListLength)
                throw new InvalidOperationException($"Wrong block access list at V2 position {i}.");
        }
    }

    /// <summary>Reads the bodies, writes the response and disposes it, as the JSON-RPC response does.</summary>
    [Benchmark]
    public async Task<int> ByRange() => await Write((await _rangeHandler.Handle(RangeStart, RangeCount)).Data);

    /// <inheritdoc cref="ByRange"/>
    [Benchmark]
    public Task<int> ByHash() => Write(_hashHandler.Handle(_hashes).Data);

    /// <summary>Reads the bodies with their block access lists, writes the response and disposes it.</summary>
    [Benchmark]
    public async Task<int> ByRangeV2() => await Write((await _rangeHandlerV2.Handle(RangeStart, RangeCount)).Data);

    /// <inheritdoc cref="ByRangeV2"/>
    [Benchmark]
    public Task<int> ByHashV2() => Write(_hashHandlerV2.Handle(_hashes).Data);

    private async Task<int> Write<T>(IReadOnlyList<T> response)
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
