// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class MainProcessingContextTests
{
    [Test]
    [CancelAfter(10000)]
    public async Task Block_tracer_registered_via_main_processing_module_is_attached_to_main_processor(CancellationToken cancellationToken)
    {
        RecordingBlockTracer recordingTracer = new();
        await using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Cancun.Instance))
            .AddSingleton<IMainProcessingModule>(new StubTracerModule(recordingTracer))
            .Build();

        await ctx.Resolve<PseudoNethermindRunner>().StartBlockProcessing(cancellationToken);
        await ctx.Resolve<TestBlockchainUtil>().AddBlockAndWaitForHead(false, cancellationToken);

        Assert.That(recordingTracer.BlocksTraced, Is.GreaterThan(0));
    }

    private sealed class StubTracerModule(IBlockTracer tracer) : Autofac.Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) => builder.AddSingleton(tracer);
    }

    private sealed class RecordingBlockTracer : BlockTracer
    {
        public int BlocksTraced { get; private set; }
        public override void StartNewBlockTrace(Block block) => BlocksTraced++;
        public override ITxTracer StartNewTxTrace(Transaction tx) => NullTxTracer.Instance;
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Test_TransactionProcessed_EventIsFired(CancellationToken cancellationToken)
    {
        await using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Cancun.Instance))
            .WithGenesisPostProcessor((_, state) =>
            {
                state.AddToBalanceAndCreateIfNotExists(TestItem.PrivateKeyA.Address, 10.Ether, Osaka.Instance);
            })
            .Build();

        IMainProcessingContext mainProcessingContext = ctx.Resolve<IMainProcessingContext>();
        int totalTransactionProcessed = 0;
        mainProcessingContext.TransactionProcessed += (_, _) => totalTransactionProcessed++;

        await ctx.Resolve<PseudoNethermindRunner>().StartBlockProcessing(cancellationToken);
        await ctx.Resolve<TestBlockchainUtil>().AddBlockAndWaitForHead(false, cancellationToken,
            Build.A.Transaction
                .WithGasLimit(100_000)
                .WithSenderAddress(TestItem.PrivateKeyA.Address)
                .WithCode(Prepare.EvmCode
                    .ForInitOf(Prepare.EvmCode
                        .PushData(TestItem.PrivateKeyB.Address)
                        .Done)
                    .Done)
                .Signed(TestItem.PrivateKeyA)
                .TestObject);

        Assert.That(totalTransactionProcessed, Is.EqualTo(1));
    }
}
