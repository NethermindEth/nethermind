// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.History;
using Nethermind.JsonRpc.Data;
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
/// One <c>eth_call</c> shaped like those replaying clients send again and again: 12 state overrides carrying 80.8 KB of
/// contract code and 11.4 KB of calldata, about 185 KB of parameters.
/// </summary>
/// <remarks>
/// <para>
/// The request is synthetic and the same on every run: addresses, calldata and code come from a seeded generator. The
/// called contract reads, from its calldata, the address and input of each of the other 11 contracts, calls them in
/// turn and returns 3,424 bytes made of what they return. Each of them hashes its input and one storage slot (set by a
/// state diff on the first) and returns nine words. Every code is padded to its size with instructions it never
/// reaches, so analysing it costs what analysing compiled code of that size costs.
/// </para>
/// <para>
/// The parameters are read from their UTF-8 text with the RPC request options, as the RPC service reads them, and the
/// call runs at the head of a test chain.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class EthCallStateOverrideBenchmarks
{
    private BasicTestBlockchain _chain = null!;
    private EthRpcModule _ethModule = null!;
    private HeadBlockSignal _headBlockSignal = null!;
    private byte[] _parameters = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // As the node registers it at startup.
        EthereumJsonSerializer.AddTypeInfoResolver(EthRpcJsonContext.Default, JsonTypeInfoResolverPriority.EthRpc);

        _chain = BasicTestBlockchain.Create(static builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance)))
            .GetAwaiter().GetResult();
        IContainer container = _chain.Container;
        ISpecProvider specProvider = _chain.SpecProvider;
        IBlockTree blockTree = _chain.BlockTree;

        _headBlockSignal = new HeadBlockSignal(blockTree);
        _ethModule = new EthRpcModule(
            container.Resolve<IJsonRpcConfig>(),
            container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge(),
            blockTree,
            blockTree,
            container.Resolve<IReceiptFinder>(),
            _chain.StateReader,
            NullTxPool.Instance,
            NullTxSender.Instance,
            NullWallet.Instance,
            LimboLogs.Instance,
            specProvider,
            new GasPriceOracle(blockTree, specProvider, LimboLogs.Instance),
            container.Resolve<IEthSyncingInfo>(),
            new FeeHistoryOracle(blockTree, NullReceiptStorage.Instance, specProvider),
            container.Resolve<IProtocolsManager>(),
            _chain.ForkInfo,
            new BlocksConfig().SecondsPerSlot,
            _headBlockSignal,
            new EthCapabilitiesProvider(
                blockTree.AsReadOnly(),
                container.Resolve<IStateBoundary>(),
                container.Resolve<ISyncConfig>(),
                Substitute.For<ISyncPointers>(),
                Substitute.For<IHistoryConfig>(),
                Substitute.For<IHistoryPruner>()),
            new BlockForRpcFactory());

        _parameters = SyntheticRequest.Parameters();

        // For comparing builds: what the call returns, and which override codes share a slot of a 256-slot
        // table indexed by the low bits of their hash (the hash seed is random per process).
        using ResultWrapper<HexBytes> result = DeserializeAndCall();
        SyntheticRequest.Check(result);
        Dictionary<Address, AccountOverride> overrides = Deserialize().Overrides;
        int[] slots = overrides.Values.Where(static o => o.Code is not null).Select(static o => ((ReadOnlySpan<byte>)o.Code).FastHash() & 255).ToArray();
        Console.WriteLine($"// eth_call: {result.Result.ResultType} {result.Result.Error} output {result.Data.Bytes.Length} B keccak {Keccak.Compute(result.Data.Bytes.Span)}");
        Console.WriteLine($"// override codes: {slots.Length}, sharing a slot of 256: {slots.Length - slots.Distinct().Count()}");
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _headBlockSignal.Dispose();
        _chain.Dispose();
    }

    /// <summary>Reads the parameters only.</summary>
    [Benchmark]
    public int ReadParameters() => Deserialize().Overrides.Count;

    /// <summary>Reads the parameters and runs the call.</summary>
    [Benchmark]
    public int ReadParametersAndCall()
    {
        using ResultWrapper<HexBytes> result = DeserializeAndCall();
        return result.Data.Bytes.Length;
    }

    private ResultWrapper<HexBytes> DeserializeAndCall()
    {
        (SignableTransactionForRpc transaction, BlockParameter block, Dictionary<Address, AccountOverride> overrides) = Deserialize();
        return _ethModule.eth_call(transaction, block, overrides);
    }

    private (SignableTransactionForRpc Transaction, BlockParameter Block, Dictionary<Address, AccountOverride> Overrides) Deserialize()
    {
        JsonSerializerOptions options = EthereumJsonSerializer.JsonRpcRequestOptions;
        Utf8JsonReader reader = new(_parameters);
        reader.Read();
        reader.Read();
        SignableTransactionForRpc transaction = JsonSerializer.Deserialize<SignableTransactionForRpc>(ref reader, options)!;
        reader.Read();
        BlockParameter block = JsonSerializer.Deserialize<BlockParameter>(ref reader, options)!;
        reader.Read();
        Dictionary<Address, AccountOverride> overrides = JsonSerializer.Deserialize<Dictionary<Address, AccountOverride>>(ref reader, options)!;
        return (transaction, block, overrides);
    }
}
