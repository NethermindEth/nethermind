// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastSync;
using Nethermind.TxPool;
using NSubstitute;
using Testably.Abstractions;

namespace Nethermind.Network.Benchmarks;

/// <summary>Serves an eth/71 GetBlockAccessLists request of random stored blocks from RocksDB and serializes the response.</summary>
/// <remarks>Lists are 0.5 to 1.5 times <see cref="AccessListSize"/> of random bytes, so a 64-hash request stops at the 2 MiB soft limit.</remarks>
[MemoryDiagnoser]
public class Eth71BlockAccessListsServingBenchmarks
{
    private const int StoredBlocks = 1024;
    private const int Handlers = 32;

    private readonly BlockAccessListsMessageSerializer _serializer = new();
    private TempPath _directory = null!;
    private IContainer _container = null!;
    private ServingHandler[] _handlers = null!;
    private IMessageSerializationService _serializationService = null!;
    private Hash256[] _hashes = null!;
    private Hash256[] _unavailableHashes = null!;
    private int _nextRequest;

    [Params(128 * 1024)]
    public int AccessListSize { get; set; }

    [Params(2, 64)]
    public int RequestedBlocks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(static _ => new HyperClockCacheWrapper())
            .AddSingleton<IDbFactory>(ctx => new RocksDbFactory(ctx.Resolve<IRocksDbConfigFactory>(), ctx.Resolve<IDbConfig>(),
                ctx.Resolve<HyperClockCacheWrapper>(), new RealFileSystem(), LimboLogs.Instance, _directory.Path))
            .Build();

        IBlockAccessListStore store = _container.Resolve<IBlockAccessListStore>();
        Random random = new(42);
        Dictionary<Hash256, ulong> numbers = [with(StoredBlocks)];
        _hashes = new Hash256[StoredBlocks];
        _unavailableHashes = new Hash256[StoredBlocks];
        for (int i = 0; i < StoredBlocks; i++)
        {
            Hash256 hash = Keccak.Compute(BitConverter.GetBytes(i));
            byte[] accessList = new byte[random.Next(AccessListSize / 2, AccessListSize * 3 / 2)];
            random.NextBytes(accessList);
            store.Insert((ulong)i, hash, accessList);
            numbers[hash] = (ulong)i;
            _hashes[i] = hash;
            _unavailableHashes[i] = Keccak.Compute(BitConverter.GetBytes(StoredBlocks + i));
        }

        random.Shuffle(_hashes);
        _serializationService = Build.A.SerializationService().WithEth71(Substitute.For<ISpecProvider>()).TestObject;
        AccessListSyncServer syncServer = new(store, numbers);
        _handlers = new ServingHandler[Handlers];
        for (int i = 0; i < Handlers; i++) _handlers[i] = new ServingHandler(_serializationService, syncServer, _serializer);
        if (Serve(0, _hashes) == 0) throw new InvalidOperationException("No access list was served.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (ServingHandler handler in _handlers) handler.Dispose();
        _container.Dispose();
        _directory.Dispose();
    }

    [Benchmark]
    public int ServeBlockAccessLists() => Serve(0, _hashes);

    [Benchmark]
    public int ServeUnavailableBlockAccessLists() => Serve(0, _unavailableHashes);

    [Benchmark]
    [Arguments(8)]
    [Arguments(32)]
    public void ServeBlockAccessListsConcurrently(int requests) =>
        Parallel.For(0, requests, i => Serve(i, _hashes));

    private int Serve(int handler, Hash256[] hashes)
    {
        int start = (int)((uint)Interlocked.Increment(ref _nextRequest) * (uint)RequestedBlocks % (uint)(StoredBlocks - RequestedBlocks));
        using GetBlockAccessListsMessage request = new(1, new ReadOnlySpan<Hash256>(hashes, start, RequestedBlocks).ToPooledList());
        IByteBuffer packet = _serializationService.ZeroSerialize(request);
        try
        {
            packet.ReadByte();
            return _handlers[handler].Serve(new NettyBufferPacket(packet) { PacketType = Eth71MessageCode.GetBlockAccessLists });
        }
        finally
        {
            packet.Release();
        }
    }

    private sealed class ServingHandler : Eth71ProtocolHandler
    {
        private readonly ISession _session;
        private int _served;

        public ServingHandler(IMessageSerializationService serializationService, ISyncServer syncServer, BlockAccessListsMessageSerializer serializer)
            : this(CreateSession(), serializationService, syncServer, serializer) { }

        private ServingHandler(ISession session, IMessageSerializationService serializationService, ISyncServer syncServer, BlockAccessListsMessageSerializer serializer)
            : base(session, serializationService, new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance), syncServer,
                RunImmediatelyScheduler.Instance, Substitute.For<ITxPool>(), Substitute.For<IGossipPolicy>(), Substitute.For<IForkInfo>(),
                LimboLogs.Instance, Substitute.For<ITxPoolConfig>(), Substitute.For<IChainHeadSpecProvider>())
        {
            _session = session;
            _statusReceived = true;
            session.When(static s => s.DeliverMessage(Arg.Any<BlockAccessListsMessage>())).Do(call =>
            {
                using BlockAccessListsMessage response = (BlockAccessListsMessage)call[0];
                int length = serializer.GetLength(response);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                _served = serializer.Serialize(buffer.AsSpan(0, length), response);
                ArrayPool<byte>.Shared.Return(buffer);
            });
        }

        /// <summary>Handles the request packet and returns the length of the serialized response.</summary>
        public int Serve(ZeroPacket packet)
        {
            _served = 0;
            HandleMessage(packet);
            _session.ClearReceivedCalls();
            return _served;
        }

        private static ISession CreateSession()
        {
            ISession session = Substitute.For<ISession>();
            session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Loopback, 30303)));
            return session;
        }
    }

    private sealed class AccessListSyncServer(IBlockAccessListStore store, Dictionary<Hash256, ulong> numbers) : ISyncServer
    {
        public MemoryManager<byte> GetBlockAccessListRlp(Hash256 blockHash) =>
            numbers.TryGetValue(blockHash, out ulong number) ? store.GetRlp(number, blockHash) : null;

        public TxReceipt[] GetReceipts(Hash256 blockHashes) => throw new NotSupportedException();
        public bool TryAppendReceipts(Hash256 blockHash, IRlpDecoder<TxReceipt> decoder, bool withBloom, ArrayPoolList<byte> destination) => throw new NotSupportedException();
        public void HintBlock(Hash256 hash, ulong number, ISyncPeer receivedFrom) { }
        public void AddNewBlock(Block block, ISyncPeer node) { }
        public MemoryManager<byte> FindBlockRlp(Hash256 hash) => null;
        public BlockHeader FindHeader(Hash256 hash) => null;
        public Hash256 FindHash(ulong number) => null;
        public IOwnedReadOnlyList<byte> FindHeadersRlp(Hash256 hash, int numberOfBlocks, int skip, bool reverse) => throw new NotSupportedException();
        public IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken, NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State) => throw new NotSupportedException();
        public int GetPeerCount() => 0;
        public ulong NetworkId => 1;
        public BlockHeader Genesis => throw new NotSupportedException();
        public BlockHeader Head => null;
        public ulong LowestBlock => 0;
        public void Dispose() { }
    }
}
