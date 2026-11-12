// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.History;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// <c>eth_call</c> on a view call that reads storage and calls a second contract: the whole request through
/// <see cref="JsonRpcProcessor"/>, the module call alone, and the call object's deserialization alone.
/// </summary>
/// <remarks>
/// <c>eth_chainId</c> and <c>eth_getBalance</c> through the processor are controls: the first touches neither the call parameters
/// nor state, the second reads state without a world state scope. The batched case runs <see cref="Batch"/> requests per invocation.
/// </remarks>
[MemoryDiagnoser]
public class EthCallPathBenchmarks
{
    private const int Batch = 16;
    private static readonly JsonRpcProcessingOptions SingleDocument = new(JsonRpcInputMode.SingleDocument);

    private TestBlockchain _chain = null!;
    private IContainer _rpcContainer = null!;
    private JsonRpcProcessor _processor = null!;
    private BufferSink _sink = null!;
    private IEthRpcModule _ethModule = null!;
    private SignableTransactionForRpc _call = null!;
    private BlockParameter _block = null!;
    private byte[] _callRequest = null!;
    private byte[] _chainIdRequest = null!;
    private byte[] _balanceRequest = null!;
    private byte[] _callObject = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _chain = await BasicTestBlockchain.Create(static builder => builder
            .AddSingleton<ILogManager>(NullLogManager.Instance)
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance) { AllowTestChainOverride = false }));

        Address target = await Deploy(_chain);
        ulong head = _chain.BlockTree.Head!.Number;
        string blockNumber = $"0x{head:x}";
        string callObject = $$"""{"from":"{{TestItem.AddressC}}","to":"{{target}}","data":"0x{{new UInt256(1).ToBigEndian().ToHexString()}}","gas":"0x100000"}""";
        _callObject = Encoding.UTF8.GetBytes(callObject);
        _callRequest = Encoding.UTF8.GetBytes($$"""{"jsonrpc":"2.0","id":1,"method":"eth_call","params":[{{callObject}},"{{blockNumber}}"]}""");
        _chainIdRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}""");
        _balanceRequest = Encoding.UTF8.GetBytes($$"""{"jsonrpc":"2.0","id":1,"method":"eth_getBalance","params":["{{TestItem.AddressC}}","{{blockNumber}}"]}""");
        _call = TypeInfoJsonSerializer.Deserialize<SignableTransactionForRpc>(_callObject, EthereumJsonSerializer.JsonOptions)!;
        _block = new BlockParameter(head);

        _ethModule = CreateEthModule(_chain);
        _rpcContainer = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new JsonRpcConfig { EnabledModules = [ModuleType.Eth] }))
            .AddSingleton<ILogManager>(NullLogManager.Instance)
            .RegisterBoundedJsonRpcModule<IEthRpcModule, AutoRpcModuleFactory<IEthRpcModule>>(1, 20000)
            .AddScoped<IEthRpcModule>(_ => CreateEthModule(_chain))
            .Build();
        _processor = new JsonRpcProcessor(_rpcContainer.Resolve<IJsonRpcService>(), new JsonRpcConfig(), Substitute.For<IFileSystem>(), NullLogManager.Instance);
        _sink = new BufferSink();

        ResultWrapper<HexBytes> result = _ethModule.eth_call(_call, _block);
        if (result.Result.ResultType != ResultType.Success || result.Data.Bytes.Length != 32 || result.Data.Bytes.Span[31] != 7)
            throw new InvalidOperationException($"Unexpected eth_call result: {result.Result.Error}");

        await EthCallRequest();
        string response = Encoding.UTF8.GetString(_sink.Written.Span);
        if (!response.Contains("\"result\":\"0x0000000000000000000000000000000000000000000000000000000000000007\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected eth_call response: {response}");
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _rpcContainer.Dispose();
        _chain.Dispose();
    }

    [Benchmark]
    public ValueTask EthCallRequest()
    {
        _sink.Reset();
        return _processor.ProcessAsync(_callRequest, new JsonRpcContext(RpcEndpoint.Http), _sink, SingleDocument);
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public async Task EthCallRequestBatched()
    {
        for (int i = 0; i < Batch; i++)
        {
            _sink.Reset();
            await _processor.ProcessAsync(_callRequest, new JsonRpcContext(RpcEndpoint.Http), _sink, SingleDocument);
        }
    }

    [Benchmark]
    public ResultWrapper<HexBytes> EthCallModule() => _ethModule.eth_call(_call, _block);

    [Benchmark]
    public SignableTransactionForRpc? EthCallObjectDeserialize() =>
        TypeInfoJsonSerializer.Deserialize<SignableTransactionForRpc>(_callObject, EthereumJsonSerializer.JsonOptions);

    [Benchmark]
    public ValueTask ChainIdRequest()
    {
        _sink.Reset();
        return _processor.ProcessAsync(_chainIdRequest, new JsonRpcContext(RpcEndpoint.Http), _sink, SingleDocument);
    }

    [Benchmark]
    public ValueTask GetBalanceRequest()
    {
        _sink.Reset();
        return _processor.ProcessAsync(_balanceRequest, new JsonRpcContext(RpcEndpoint.Http), _sink, SingleDocument);
    }

    /// <summary>Deploys a contract that reads two of its slots and static-calls a second contract that reads one of its own.</summary>
    private static async Task<Address> Deploy(TestBlockchain chain)
    {
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Address inner = ContractAddress.From(TestItem.AddressB, nonce);
        Address outer = ContractAddress.From(TestItem.AddressB, nonce + 1);

        byte[] innerRuntime = Prepare.EvmCode
            .PushData(0).Op(Instruction.SLOAD).PushData(0).Op(Instruction.MSTORE)
            .PushData(32).PushData(0).Op(Instruction.RETURN).Done;
        byte[] outerRuntime = Prepare.EvmCode
            .PushData(0).Op(Instruction.CALLDATALOAD).Op(Instruction.SLOAD).Op(Instruction.POP)
            .StaticCall(inner, 50_000).Op(Instruction.POP)
            .PushData(2).Op(Instruction.SLOAD).PushData(0).Op(Instruction.MSTORE)
            .PushData(32).PushData(0).Op(Instruction.RETURN).Done;

        await chain.AddBlock(
            Build.A.Transaction.WithNonce(nonce).WithGasLimit(1_000_000)
                .WithCode(Prepare.EvmCode.PersistData("0x00", "0x05").ForInitOf(innerRuntime).Done)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            Build.A.Transaction.WithNonce(nonce + 1).WithGasLimit(1_000_000)
                .WithCode(Prepare.EvmCode.PersistData("0x01", "0x03").PersistData("0x02", "0x07").ForInitOf(outerRuntime).Done)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject);

        return outer;
    }

    private static EthRpcModule CreateEthModule(TestBlockchain chain)
    {
        IContainer container = chain.Container;
        IBlockTree blockTree = chain.BlockTree;
        return new EthRpcModule(
            new JsonRpcConfig(),
            container.Resolve<IBlockchainBridge>(),
            chain.BlockFinder,
            blockTree,
            container.Resolve<IReceiptFinder>(),
            chain.StateReader,
            NullTxPool.Instance,
            NullTxSender.Instance,
            NullWallet.Instance,
            NullLogManager.Instance,
            chain.SpecProvider,
            container.Resolve<IGasPriceOracle>(),
            container.Resolve<IEthSyncingInfo>(),
            new FeeHistoryOracle(blockTree, NullReceiptStorage.Instance, chain.SpecProvider, new JsonRpcConfig()),
            container.Resolve<IProtocolsManager>(),
            chain.ForkInfo,
            new BlocksConfig().SecondsPerSlot,
            new HeadBlockSignal(blockTree),
            new EthCapabilitiesProvider(
                blockTree.AsReadOnly(),
                container.Resolve<IStateBoundary>(),
                container.Resolve<ISyncConfig>(),
                Substitute.For<ISyncPointers>(),
                Substitute.For<IHistoryConfig>(),
                Substitute.For<IHistoryPruner>()),
            new BlockForRpcFactory());
    }

    private sealed class BufferSink : IJsonRpcResponseSink
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(4096);

        public ReadOnlyMemory<byte> Written => _buffer.WrittenMemory;
        public long BytesWritten => _buffer.WrittenCount;
        public bool StopRequested => false;

        public void Reset() => _buffer.ResetWrittenCount();

        public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            JsonRpcResponseWriter.Write(_buffer, response, EthereumJsonSerializer.JsonOptions);
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginBatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask EndBatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
