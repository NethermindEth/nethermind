// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Trace;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class AuRaRewardContractCallLimitTests
{
    private static readonly Address RewardContract = new("0xc3000000000000000000000000000000000000c3");

    [Test]
    public async Task Trace_call_reports_the_capped_reward_failure_before_streaming([Values] bool streaming)
    {
        ITransactionProcessor rewardProcessor = Substitute.For<ITransactionProcessor>();
        rewardProcessor.When(processor => processor.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()))
            .Do(call => call.Arg<ITxTracer>().MarkAsFailed(RewardContract, 0, [], "out of gas"));
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder =>
            builder.AddSingleton<IReadOnlyQueryModule>(new AuRaRewardContractModule(rewardProcessor)));
        chain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Dictionary<Address, AccountOverride> stateOverride = new()
        {
            [RewardContract] = new AccountOverride { Code = [0x00] }
        };
        TransactionForRpc call = new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressC, Gas = 100_000 };

        ContractCallFailedException? error = Assert.Throws<ContractCallFailedException>(() =>
            chain.TraceRpcModule.trace_call(call, ["trace"], BlockParameter.Latest, stateOverride));

        Assert.That(error!.Message, Does.StartWith("System call to RewardContract.reward returned error 'out of gas'"));
        rewardProcessor.Received(1).Execute(
            Arg.Is<Transaction>(tx => tx.To == RewardContract && tx.GasLimit == CallableContract.SystemTransactionGasLimit),
            Arg.Any<ITxTracer>());
    }

    [Test]
    public async Task Trace_call_with_an_active_reward_contract_uses_the_buffered_trace_limit()
    {
        ITransactionProcessor rewardProcessor = Substitute.For<ITransactionProcessor>();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder =>
            builder.AddSingleton<IReadOnlyQueryModule>(new AuRaRewardContractModule(rewardProcessor)));
        IJsonRpcConfig config = chain.Container.Resolve<IJsonRpcConfig>();
        config.EnableTracingStreamMode = true;
        config.MaxBufferedTraceLogSize = 64;
        Dictionary<Address, AccountOverride> stateOverride = new()
        {
            [RewardContract] = new AccountOverride { Code = [0x00] }
        };
        TransactionForRpc call = new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressC, Gas = 100_000 };

        Assert.That(() => chain.TraceRpcModule.trace_call(call, ["trace"], BlockParameter.Latest, stateOverride),
            Throws.TypeOf<TraceDataLimitExceededException>());
        rewardProcessor.DidNotReceive().Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
    }

    [Test]
    public async Task Trace_call_with_overrides_streams_without_an_active_reward_contract([Values] bool hasFutureContract)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder =>
        {
            if (hasFutureContract)
                builder.AddSingleton<IReadOnlyQueryModule>(new AuRaRewardContractModule(Substitute.For<ITransactionProcessor>(), ulong.MaxValue));
        });
        chain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = true;
        Dictionary<Address, AccountOverride> stateOverride = new()
        {
            [TestItem.AddressC] = new AccountOverride { Code = [0x00] }
        };
        TransactionForRpc call = new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressC, Gas = 100_000 };

        using ResultWrapper<ParityTxTraceFromReplay> result = chain.TraceRpcModule.trace_call(call, ["trace"], BlockParameter.Latest, stateOverride);

        Assert.That(result.Data, Is.TypeOf<ParityTxTraceFromReplayStreamingResult>());
    }

    private sealed class AuRaRewardContractModule(ITransactionProcessor rewardProcessor, ulong transition = 0) : Module, IReadOnlyQueryModule
    {
        public ReadOnlyQueryKind AppliesTo => ReadOnlyQueryKind.Trace;

        protected override void Load(ContainerBuilder builder) => builder
            .AddSingleton(new AuRaChainSpecEngineParameters
            {
                BlockRewardContractAddress = RewardContract,
                BlockRewardContractTransition = transition,
                BlockReward = new SortedDictionary<ulong, UInt256> { { 0, UInt256.Zero } },
            })
            .AddScoped<IRewardCalculator>(ctx => new AuRaRewardCalculator(
                ctx.Resolve<AuRaChainSpecEngineParameters>(), rewardProcessor, ctx.ResolveOptional<RewardContractGasLimit>()));
    }
}
