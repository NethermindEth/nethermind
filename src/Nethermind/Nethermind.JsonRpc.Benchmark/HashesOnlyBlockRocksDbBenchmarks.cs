// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Testably.Abstractions;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Hashes-only reads of distinct mainnet-sized blocks (about 148 KB each) from a RocksDB blocks database, bypassing
/// the reader's cache so that every read goes to the database, by one reader or by several concurrent ones.
/// </summary>
[MemoryDiagnoser]
public class HashesOnlyBlockRocksDbBenchmarks
{
    private const int Blocks = 256;
    private const int TransactionsPerBlock = 128;
    private const int CallDataLength = 1024;
    private const int TransactionPool = 1024;

    private TempPath _directory = null!;
    private IDb _blockDb = null!;
    private HashesOnlyBlockReader _reader = null!;
    private (ulong Number, Hash256 Hash)[] _blocks = null!;

    [Params(1, 8, 16)]
    public int Readers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        RocksDbConfigFactory configFactory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(8L * 1024 * 1024 * 1024), NullLogManager.Instance, validateConfig: false);
        _blockDb = new RocksDbFactory(configFactory, dbConfig, new HyperClockCacheWrapper(), new RealFileSystem(), NullLogManager.Instance, _directory.Path)
            .CreateDb(new DbSettings(DbNames.Blocks, DbNames.Blocks));
        BlockStore blockStore = new(_blockDb);
        _reader = new HashesOnlyBlockReader(_blockDb, new HeaderDecoder(), new ChainLevelInfoRepository(new MemDb()),
            new HeaderStore(new MemDb(), new MemDb()), Build.A.BlockTree().TestObject, MainnetSpecProvider.Instance);

        Transaction[] pool = BuildTransactionPool();
        _blocks = new (ulong, Hash256)[Blocks];
        long storedBytes = 0;
        for (int b = 0; b < Blocks; b++)
        {
            Transaction[] transactions = new Transaction[TransactionsPerBlock];
            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = pool[(b * 7 + i) % pool.Length];
            }

            Block block = Build.A.Block.WithNumber(25_000_000UL + (ulong)b).WithBaseFeePerGas(10 * Unit.GWei)
                .WithTransactions(transactions).WithWithdrawals(16).TestObject;
            blockStore.Insert(block);
            _blocks[b] = (block.Number, block.Hash!);
        }

        _blockDb.Flush();

        foreach ((ulong number, Hash256 hash) in _blocks)
        {
            HashesOnlyBlock read = _reader.Read(number, hash) ?? throw new InvalidOperationException($"Block {number} does not read.");
            if (read.TransactionHashes.Length != TransactionsPerBlock)
                throw new InvalidOperationException($"Block {number} reads {read.TransactionHashes.Length} transactions.");
            storedBytes += read.Block.EncodedSize!.Value;
        }

        Console.WriteLine($"// Stored block value: {storedBytes / Blocks} bytes on average");
    }

    [Benchmark(OperationsPerInvoke = Blocks)]
    public int Read()
    {
        if (Readers == 1)
        {
            int count = 0;
            foreach ((ulong number, Hash256 hash) in _blocks)
            {
                count += _reader.Read(number, hash)!.TransactionHashes.Length;
            }

            return count;
        }

        Parallel.For(0, Blocks, new ParallelOptions { MaxDegreeOfParallelism = Readers }, i =>
        {
            (ulong number, Hash256 hash) = _blocks[i];
            _ = _reader.Read(number, hash)!;
        });
        return Blocks;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
        _blockDb.Dispose();
        _directory.Dispose();
    }

    private static Transaction[] BuildTransactionPool()
    {
        Transaction[] pool = new Transaction[TransactionPool];
        for (int i = 0; i < pool.Length; i++)
        {
            byte[] callData = new byte[CallDataLength];
            new Random(i).NextBytes(callData);
            pool[i] = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(BlockchainIds.Mainnet)
                .WithNonce((ulong)i)
                .WithGasLimit(200_000)
                .WithMaxFeePerGas(30 * Unit.GWei)
                .WithMaxPriorityFeePerGas(1 * Unit.GWei)
                .WithValue(1 * Unit.Ether)
                .WithTo(TestItem.Addresses[i % TestItem.Addresses.Length])
                .WithData(callData)
                .SignedAndResolved()
                .TestObject;
        }

        return pool;
    }
}
