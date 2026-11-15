// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.History;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Test;
using NSubstitute;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures answering one <c>GetBlockHeaders</c> request for headers older than the block tree's head cache:
/// finding the headers and serializing the response.
/// </summary>
[MemoryDiagnoser]
public class BlockHeadersServingBenchmarks
{
    private const int MaxCount = 1024;

    // Enough newer blocks to keep the served ones out of the block tree's head cache window.
    private const int NewerBlocks = 200;

    private readonly BlockHeadersMessageSerializer _serializer = new();
    private BlockTree _blockTree = null!;
    private SyncServer _server = null!;
    private Hash256 _startHash = null!;

    [Params(192, 1024)]
    public int Count { get; set; }

    [Params(false, true)]
    public bool Reverse { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(1);
        _blockTree = builder.TestObject;
        Block parent = _blockTree.Head!;
        for (int i = 0; i < MaxCount + NewerBlocks; i++)
        {
            Block block = Build.A.Block
                .WithParent(parent)
                .WithBaseFeePerGas(7)
                .WithWithdrawalsRoot(TestItem.KeccakA)
                .WithBlobGasUsed(131072)
                .WithExcessBlobGas(0)
                .WithParentBeaconBlockRoot(TestItem.KeccakB)
                .WithRequestsHash(TestItem.KeccakC)
                .TestObject;
            _blockTree.SuggestBlock(block);
            _blockTree.TryUpdateMainChain(block.Header, true, false, block);
            parent = block;
        }

        _startHash = _blockTree.FindHash(Reverse ? (ulong)Count : 1)!;
        ((IClearableCache)builder.HeaderStore).ClearCache();
        _server = new SyncServer(
            Substitute.For<IWorldStateManager>(),
            new MemDb(),
            _blockTree,
            builder.HeaderStore,
            new HeaderDecoder(),
            NullReceiptStorage.Instance,
            Substitute.For<IBlockAccessListStore>(),
            Always.Valid,
            Always.Valid,
            Substitute.For<ISyncPeerPool>(),
            StaticSelector.Full,
            new TestSyncConfig(),
            Policy.FullGossip,
            Substitute.For<IHistoryPruner>(),
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);

        if (Decoded() != Encoded()) throw new InvalidOperationException("The two paths serve different responses.");
    }

    [GlobalCleanup]
    public void Cleanup() => _server.Dispose();

    /// <summary>Decodes each stored header and encodes it again, as serving did before.</summary>
    [Benchmark(Baseline = true)]
    public int Decoded()
    {
        using BlockHeadersMessage message = new(_blockTree.FindHeaders(_startHash, Count, 0, Reverse));
        return Serialize(message);
    }

    /// <summary>Copies each stored header as it is.</summary>
    [Benchmark]
    public int Encoded()
    {
        using BlockHeadersMessage message = new() { EncodedHeaders = _server.FindHeadersRlp(_startHash, Count, 0, Reverse) };
        return Serialize(message);
    }

    private int Serialize(BlockHeadersMessage message)
    {
        int length = _serializer.GetLength(message);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        int written = _serializer.Serialize(buffer, message);
        ArrayPool<byte>.Shared.Return(buffer);
        return written;
    }
}
