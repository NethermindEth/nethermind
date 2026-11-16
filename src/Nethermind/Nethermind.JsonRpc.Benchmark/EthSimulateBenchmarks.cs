// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>Measures <c>eth_simulateV1</c> for a call that makes many calls to an account without code and to a precompile.</summary>
[MemoryDiagnoser]
public class EthSimulateBenchmarks
{
    private const int CallPairs = 500;
    private static readonly Address Sender = new("0x1000000000000000000000000000000000000001");
    private static readonly Address Caller = new("0xc700000000000000000000000000000000000000");
    private static readonly Address AccountWithoutCode = new("0xc800000000000000000000000000000000000000");
    private static readonly Address IdentityPrecompile = new("0x0000000000000000000000000000000000000004");

    private IContainer _container = null!;
    private IBlockchainBridge _bridge = null!;
    private IBlockFinder _blockFinder = null!;
    private ISpecProvider _specProvider = null!;
    private byte[] _code = null!;

    [Params(false, true)]
    public bool TraceTransfers { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        TestSpecProvider specProvider = new(Frontier.Instance) { NextForkSpec = London.Instance, ForkOnBlockNumber = 2 };
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(specProvider)
            .Build();

        IWorldState stateProvider = _container.Resolve<IMainProcessingContext>().WorldState;
        stateProvider.CreateAccount(Address.Zero, 1000.Ether);
        stateProvider.Commit(specProvider.GenesisSpec);
        stateProvider.CommitTree(0);

        Block genesisBlock = Build.A.Block.Genesis.TestObject;
        IBlockTree blockTree = _container.Resolve<IBlockTree>();
        blockTree.SuggestBlock(genesisBlock);
        Block block1 = Build.A.Block.WithParent(genesisBlock).WithNumber(1).TestObject;
        blockTree.SuggestBlock(block1);

        IBlockchainProcessor blockchainProcessor = _container.Resolve<IMainProcessingContext>().BlockchainProcessor;
        blockchainProcessor.Process(genesisBlock, ProcessingOptions.None, NullBlockTracer.Instance);
        blockchainProcessor.Process(block1, ProcessingOptions.None, NullBlockTracer.Instance);

        _bridge = _container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge();
        _blockFinder = blockTree;
        _specProvider = specProvider;

        // CALL(gas, account without code, 0, 0, 0, 0, 0) and STATICCALL(gas, identity precompile, 0, 0, 0, 0), discarding the flags.
        StringBuilder code = new();
        for (int i = 0; i < CallPairs; i++)
        {
            code.Append("6000600060006000600073").Append(AccountWithoutCode.Bytes.ToHexString()).Append("5af150");
            code.Append("600060006000600073").Append(IdentityPrecompile.Bytes.ToHexString()).Append("5afa50");
        }
        _code = Bytes.FromHexString(code.Append("00").ToString());

        SimulateCallResult call = Simulate()[0].Calls.Single();
        if (call.Error is not null) throw new InvalidOperationException(call.Error.Message);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _container.Dispose();

    [Benchmark]
    public IReadOnlyList<SimulateBlockResult<SimulateCallResult>> Simulate()
    {
        SimulatePayload<TransactionForRpc> payload = new()
        {
            TraceTransfers = TraceTransfers,
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { Sender, new AccountOverride { Balance = 1.Ether } },
                        { Caller, new AccountOverride { Code = _code } }
                    },
                    Calls = [new LegacyTransactionForRpc { From = Sender, To = Caller, Gas = 10_000_000, GasPrice = 0 }]
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            new SimulateTxExecutor<SimulateCallResult>(_bridge, _blockFinder, new JsonRpcConfig(), _specProvider, new SimulateBlockMutatorTracerFactory())
                .Execute(payload, BlockParameter.Latest);
        return result.Data ?? throw new InvalidOperationException(result.Result.Error);
    }
}
