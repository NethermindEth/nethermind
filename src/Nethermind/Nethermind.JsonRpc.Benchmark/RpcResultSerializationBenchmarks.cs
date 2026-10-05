// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Filters;
using Nethermind.History;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Full and hashes-only block responses through the generated writers and through the metadata path, split into building
/// the RPC objects, serializing them, and the whole <c>eth_getBlockByNumber</c> call as the RPC module answers it.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class RpcResultSerializationBenchmarks
{
    private const string Generated = "generated writers";
    private const string Metadata = "metadata path";

    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);
    private JsonSerializerOptions _generated = null!;
    private JsonSerializerOptions _metadata = null!;
    private Block _block = null!;
    private BlockForRpc _full = null!;
    private BlockForRpc _hashes = null!;
    private FilterLog[] _logs = null!;
    private IContainer _container = null!;
    private HeadBlockSignal _headBlockSignal = null!;
    private EthRpcModule _ethModule = null!;

    [GlobalSetup]
    public void Setup()
    {
        _block = BuildBlock(200);
        _full = new BlockForRpc(_block, includeFullTransactionData: true, MainnetSpecProvider.Instance);
        _hashes = new BlockForRpc(_block, includeFullTransactionData: false, MainnetSpecProvider.Instance);
        _logs = BuildLogs(1000);

        // Read after Facade's module initializer registered the writers, which rebuilds the options.
        _generated = EthereumJsonSerializer.JsonOptions;
        _metadata = GeneratedJsonWriters.GetMetadataOptions(_generated);
        foreach (IGeneratedJsonWriter writer in _generated.Converters.OfType<IGeneratedJsonWriter>())
        {
            if (!writer.IsActive(_generated)) throw new InvalidOperationException($"{writer.GetType().Name} defers to the metadata path");
        }

        if (!GeneratedJsonWriters.TryGetDispatchWriter(typeof(Facade.Eth.RpcTransaction.EIP1559TransactionForRpc), out IGeneratedJsonWriter? txWriter) || !txWriter.IsActive(_generated))
        {
            throw new InvalidOperationException("the EIP-1559 transaction writer is not reached through the dispatch");
        }

        SetUpRpcModule();

        Check("full block", () => Serialize(_full, _generated), () => Serialize(_full, _metadata));
        Check("hashes block", () => Serialize(_hashes, _generated), () => Serialize(_hashes, _metadata));
        Check("logs", () => Serialize(_logs, _generated), () => Serialize(_logs, _metadata));
        Check("rpc full", () => Rpc(true, _generated), () => Rpc(true, _metadata));
        Check("rpc hashes", () => Rpc(false, _generated), () => Rpc(false, _metadata));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _headBlockSignal.Dispose();
        _container.Dispose();
    }

    [BenchmarkCategory("build BlockForRpc"), Benchmark(Baseline = true, Description = "full, 200 txs")]
    public BlockForRpc BuildFull() => new(_block, includeFullTransactionData: true, MainnetSpecProvider.Instance);

    [BenchmarkCategory("build BlockForRpc"), Benchmark(Description = "hashes")]
    public BlockForRpc BuildHashes() => new(_block, includeFullTransactionData: false, MainnetSpecProvider.Instance);

    [BenchmarkCategory("serialize full block"), Benchmark(Baseline = true, Description = Metadata)]
    public int SerializeFullMetadata() => Serialize(_full, _metadata);

    [BenchmarkCategory("serialize full block"), Benchmark(Description = Generated)]
    public int SerializeFullGenerated() => Serialize(_full, _generated);

    [BenchmarkCategory("serialize hashes block"), Benchmark(Baseline = true, Description = Metadata)]
    public int SerializeHashesMetadata() => Serialize(_hashes, _metadata);

    [BenchmarkCategory("serialize hashes block"), Benchmark(Description = Generated)]
    public int SerializeHashesGenerated() => Serialize(_hashes, _generated);

    [BenchmarkCategory("eth_getBlockByNumber full"), Benchmark(Baseline = true, Description = Metadata)]
    public int RpcFullMetadata() => Rpc(true, _metadata);

    [BenchmarkCategory("eth_getBlockByNumber full"), Benchmark(Description = Generated)]
    public int RpcFullGenerated() => Rpc(true, _generated);

    [BenchmarkCategory("eth_getBlockByNumber hashes"), Benchmark(Baseline = true, Description = Metadata)]
    public int RpcHashesMetadata() => Rpc(false, _metadata);

    [BenchmarkCategory("eth_getBlockByNumber hashes"), Benchmark(Description = Generated)]
    public int RpcHashesGenerated() => Rpc(false, _generated);

    [BenchmarkCategory("serialize 1000 logs"), Benchmark(Baseline = true, Description = Metadata)]
    public int LogsMetadata() => Serialize(_logs, _metadata);

    [BenchmarkCategory("serialize 1000 logs"), Benchmark(Description = Generated)]
    public int LogsGenerated() => Serialize(_logs, _generated);

    private int Serialize<T>(T value, JsonSerializerOptions options)
    {
        _buffer.ResetWrittenCount();
        using (Utf8JsonWriter writer = new(_buffer, new JsonWriterOptions { SkipValidation = true, Encoder = options.Encoder }))
        {
            TypeInfoJsonSerializer.Serialize(writer, value, options);
        }

        return _buffer.WrittenCount;
    }

    private int Rpc(bool fullTransactions, JsonSerializerOptions options)
    {
        ResultWrapper<BlockForRpc> result = _ethModule.eth_getBlockByNumber(new BlockParameter((ulong)_block.Number), fullTransactions);
        using JsonRpcSuccessResponse response = new() { Id = 1, Result = result.Data };
        _buffer.ResetWrittenCount();
        JsonRpcResponseWriter.Write(_buffer, response, options);
        return _buffer.WrittenCount;
    }

    private void Check(string name, Func<int> generated, Func<int> metadata)
    {
        generated();
        byte[] expected = _buffer.WrittenSpan.ToArray();
        metadata();
        if (!expected.AsSpan().SequenceEqual(_buffer.WrittenSpan)) throw new InvalidOperationException($"{name}: generated writers changed the output");
        if (expected.Length < 100) throw new InvalidOperationException($"{name}: suspiciously short output");
    }

    private void SetUpRpcModule()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
            .Build();

        IBlockTree blockTree = _container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.Genesis.TestObject;
        blockTree.SuggestBlock(genesis, BlockTreeSuggestOptions.None);
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, forceUpdateHeadBlock: true, genesis);

        // Re-parent the benchmark block onto genesis and make it canonical without processing it.
        _block = Build.A.Block.WithParent(genesis).WithNumber(1).WithBaseFeePerGas(10 * Unit.GWei).WithTransactions(_block.Transactions).TestObject;
        _full = new BlockForRpc(_block, includeFullTransactionData: true, MainnetSpecProvider.Instance);
        _hashes = new BlockForRpc(_block, includeFullTransactionData: false, MainnetSpecProvider.Instance);
        blockTree.SuggestBlock(_block, BlockTreeSuggestOptions.None);
        blockTree.TryUpdateMainChain(_block.Header, wereProcessed: true, forceUpdateHeadBlock: true, _block);

        ISpecProvider specProvider = _container.Resolve<ISpecProvider>();
        _headBlockSignal = new HeadBlockSignal(blockTree);
        _ethModule = new EthRpcModule(
            _container.Resolve<IJsonRpcConfig>(),
            _container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge(),
            blockTree,
            blockTree,
            _container.Resolve<IReceiptFinder>(),
            _container.Resolve<IStateReader>(),
            NullTxPool.Instance,
            NullTxSender.Instance,
            NullWallet.Instance,
            LimboLogs.Instance,
            specProvider,
            _container.Resolve<IGasPriceOracle>(),
            _container.Resolve<IEthSyncingInfo>(),
            new FeeHistoryOracle(blockTree, NullReceiptStorage.Instance, specProvider),
            _container.Resolve<IProtocolsManager>(),
            _container.Resolve<IForkInfo>(),
            new BlocksConfig().SecondsPerSlot,
            _headBlockSignal,
            new EthCapabilitiesProvider(
                blockTree.AsReadOnly(),
                _container.Resolve<IStateBoundary>(),
                _container.Resolve<ISyncConfig>(),
                Substitute.For<ISyncPointers>(),
                Substitute.For<IHistoryConfig>(),
                Substitute.For<IHistoryPruner>()),
            new BlockForRpcFactory());

        if (_ethModule.eth_getBlockByNumber(new BlockParameter((ulong)_block.Number), true).Data?.Hash != _block.Hash)
        {
            throw new InvalidOperationException("eth_getBlockByNumber does not return the benchmark block");
        }
    }

    private static FilterLog[] BuildLogs(int count)
    {
        FilterLog[] logs = new FilterLog[count];
        Random random = new(42);
        for (int i = 0; i < count; i++)
        {
            byte[] data = new byte[64];
            random.NextBytes(data);
            logs[i] = new FilterLog(i, 25_000_000UL + (ulong)(i / 100), 1_700_000_000UL, TestItem.KeccakC, i % 200, TestItem.KeccakD,
                TestItem.Addresses[i % TestItem.Addresses.Length], data, [TestItem.Keccaks[i % TestItem.Keccaks.Length], TestItem.KeccakA, TestItem.KeccakB]);
        }

        return logs;
    }

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

        return Build.A.Block.WithNumber(25_000_000).WithBaseFeePerGas(10 * Unit.GWei).WithTransactions(transactions).TestObject;
    }
}
