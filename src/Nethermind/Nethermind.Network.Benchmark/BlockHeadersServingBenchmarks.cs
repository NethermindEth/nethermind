// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.History;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization;
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
    private IContainer _container = null!;
    private IBlockTree _blockTree = null!;
    private ISyncServer _server = null!;
    private Hash256 _startHash = null!;

    [Params(192, 1024)]
    public int Count { get; set; }

    [Params(false, true)]
    public bool Reverse { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Prague.Instance))
            .AddSingleton<IHistoryPruner>(Substitute.For<IHistoryPruner>())
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<ISealValidator>(Always.Valid)
            .Build();
        _blockTree = _container.Resolve<IBlockTree>();

        Block parent = Build.A.Block.Genesis.TestObject;
        _blockTree.SuggestBlock(parent);
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
        ((IClearableCache)_container.Resolve<IHeaderStore>()).ClearCache();
        _server = _container.Resolve<ISyncServer>();

        if (Decoded() != Encoded()) throw new InvalidOperationException("The two paths serve different responses.");
    }

    [GlobalCleanup]
    public void Cleanup() => _container.Dispose();

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
