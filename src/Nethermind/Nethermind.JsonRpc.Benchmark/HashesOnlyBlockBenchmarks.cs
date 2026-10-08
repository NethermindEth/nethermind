// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Repositories;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Building the hashes-only <c>eth_getBlockByNumber</c> response for a stored block outside the head window: decoding
/// the whole block as the block tree does, reading only what the response needs, and serving a repeat from the cache,
/// where the cached hashes are flat (<see cref="BlockTransactions.FromHashes"/>) or one <see cref="Hash256"/> each.
/// </summary>
[MemoryDiagnoser]
public class HashesOnlyBlockBenchmarks
{
    private readonly BlockForRpcFactory _factory = new();
    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);
    private Hash256[] _hashObjects = null!;
    private BlockStore _blockStore = null!;
    private HashesOnlyBlockReader _reader = null!;
    private HashesOnlyBlock _cached = null!;
    private Block _block = null!;

    [Params(250)]
    public int Transactions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        MemDb blockDb = new();
        _blockStore = new BlockStore(blockDb);
        _reader = new HashesOnlyBlockReader(blockDb, new HeaderDecoder(), new ChainLevelInfoRepository(new MemDb()),
            new HeaderStore(new MemDb(), new MemDb()), Build.A.BlockTree().TestObject, MainnetSpecProvider.Instance);
        _block = BuildBlock(Transactions);
        _blockStore.Insert(_block);
        _cached = _reader.Read(_block.Number, _block.Hash!)!;
        _hashObjects = Array.ConvertAll(_cached.TransactionHashes, static hash => new Hash256(in hash));
    }

    [Benchmark(Baseline = true)]
    public BlockForRpc DecodeBlock()
    {
        Block block = _blockStore.Get(_block.Number, _block.Hash!)!;
        return _factory.Create(block, includeFullTransactionData: false, MainnetSpecProvider.Instance);
    }

    [Benchmark]
    public BlockForRpc ReadHashesOnly() => Create(_reader.Read(_block.Number, _block.Hash!)!);

    [Benchmark]
    public BlockForRpc Cached() => Create(_cached);

    [Benchmark]
    public BlockForRpc CachedHashObjects() => CreateWithHashObjects();

    [Benchmark]
    public int CachedAndSerialized() => Serialize(Create(_cached));

    [Benchmark]
    public int CachedHashObjectsAndSerialized() => Serialize(CreateWithHashObjects());

    private BlockForRpc Create(HashesOnlyBlock hashesOnly)
    {
        BlockForRpc block = _factory.Create(hashesOnly.Block, includeFullTransactionData: false, MainnetSpecProvider.Instance, skipTxs: true);
        block.Transactions = BlockTransactions.FromHashes(hashesOnly.TransactionHashes);
        return block;
    }

    private BlockForRpc CreateWithHashObjects()
    {
        BlockForRpc block = _factory.Create(_cached.Block, includeFullTransactionData: false, MainnetSpecProvider.Instance, skipTxs: true);
        block.Transactions = _hashObjects;
        return block;
    }

    private int Serialize(BlockForRpc block)
    {
        _buffer.ResetWrittenCount();
        using Utf8JsonWriter writer = new(_buffer);
        TypeInfoJsonSerializer.Serialize(writer, block, EthereumJsonSerializer.JsonOptions);
        return _buffer.WrittenCount;
    }

    // The call data mix of RpcResultSerializationBenchmarks: half transfers, the rest 68 bytes to 8 KB.
    private static Block BuildBlock(int count)
    {
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            int callDataLength = (i % 20) switch
            {
                < 10 => 0,
                < 14 => 68,
                < 17 => 260,
                < 19 => 1024,
                _ => 8192,
            };

            TransactionBuilder<Transaction> builder = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(BlockchainIds.Mainnet)
                .WithNonce((ulong)i)
                .WithGasLimit(callDataLength == 0 ? 21_000UL : 90_000UL + (ulong)callDataLength * 16)
                .WithMaxFeePerGas(30 * Unit.GWei)
                .WithMaxPriorityFeePerGas(1 * Unit.GWei)
                .WithValue(1 * Unit.Ether)
                .WithTo(TestItem.Addresses[i % TestItem.Addresses.Length]);

            if (callDataLength > 0)
            {
                byte[] callData = new byte[callDataLength];
                new Random(i).NextBytes(callData);
                builder = builder.WithData(callData);
            }

            transactions[i] = builder.SignedAndResolved().TestObject;
        }

        return Build.A.Block.WithNumber(25_000_000).WithBaseFeePerGas(10 * Unit.GWei).WithTransactions(transactions)
            .WithWithdrawals(16).TestObject;
    }
}
