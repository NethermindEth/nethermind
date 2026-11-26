// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.SnapServer;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>Serves a snap GetBlockAccessLists request of random stored blocks from RocksDB.</summary>
/// <remarks>Each call asks for other blocks, and the header cache is cleared after the blocks are stored.</remarks>
[MemoryDiagnoser]
public class SnapBlockAccessListsServingBenchmark
{
    private const int StoredBlocks = 2048;

    private TempPath _directory = null!;
    private IContainer _container = null!;
    private ISnapServer _server = null!;
    private ValueHash256[][] _requests = null!;
    private ValueHash256[] _unknownRequest = null!;
    private int _nextRequest;

    [Params(2_000, 30_000, 131_072)]
    public int AccessListSize { get; set; }

    [Params(4_096, ISnapStateServer.HardResponseByteLimit)]
    public long ByteLimit { get; set; }

    [Params(2, 64)]
    public int RequestedBlocks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IDbFactory>(ctx => new RocksDbFactory(ctx.Resolve<IRocksDbConfigFactory>(), ctx.Resolve<IDbConfig>(),
                new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path))
            .Build();

        IHeaderStore headerStore = _container.Resolve<IHeaderStore>();
        IBlockAccessListStore accessListStore = _container.Resolve<IBlockAccessListStore>();
        Random random = new(42);
        ValueHash256[] hashes = new ValueHash256[StoredBlocks];
        for (int i = 0; i < StoredBlocks; i++)
        {
            BlockHeader header = Build.A.BlockHeader
                .WithNumber(1_000_000 + i)
                .WithBlockAccessListHash(TestItem.KeccakA)
                .TestObject;
            byte[] accessList = new byte[random.Next(AccessListSize / 2, AccessListSize * 3 / 2)];
            random.NextBytes(accessList);
            headerStore.Insert(header);
            accessListStore.Insert(header.Number, header.Hash!, accessList);
            hashes[i] = header.Hash!.ValueHash256;
        }

        ((IClearableCache)headerStore).ClearCache();
        random.Shuffle(hashes);
        _requests = new ValueHash256[StoredBlocks / RequestedBlocks][];
        for (int i = 0; i < _requests.Length; i++)
        {
            _requests[i] = hashes[(i * RequestedBlocks)..((i + 1) * RequestedBlocks)];
        }

        _unknownRequest = new ValueHash256[RequestedBlocks];
        for (int i = 0; i < RequestedBlocks; i++)
        {
            _unknownRequest[i] = Keccak.Compute(BitConverter.GetBytes(-1 - i)).ValueHash256;
        }

        _server = _container.Resolve<ISnapServer>();
        using IByteArrayList lists = _server.GetBlockAccessLists(_requests[0], ByteLimit, CancellationToken.None);
        if (lists.Count == 0 || lists[0].Length == 0) throw new InvalidOperationException("No access list was served.");
    }

    [Benchmark]
    public int ServeBlockAccessLists()
    {
        ValueHash256[] request = _requests[(uint)Interlocked.Increment(ref _nextRequest) % (uint)_requests.Length];
        using IByteArrayList lists = _server.GetBlockAccessLists(request, ByteLimit, CancellationToken.None);
        return lists.Count;
    }

    [Benchmark]
    public int ServeUnknownBlockAccessLists()
    {
        using IByteArrayList lists = _server.GetBlockAccessLists(_unknownRequest, ByteLimit, CancellationToken.None);
        return lists.Count;
    }

    [Benchmark]
    [Arguments(8)]
    [Arguments(32)]
    public void ServeBlockAccessListsConcurrently(int requests) =>
        Parallel.For(0, requests, _ => ServeBlockAccessLists());

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        _directory.Dispose();
    }
}
