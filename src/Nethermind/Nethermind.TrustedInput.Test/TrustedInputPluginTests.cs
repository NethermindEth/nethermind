// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;
using Input = Nethermind.Stateless.Execution.IO.TrustedInput;

namespace Nethermind.TrustedInput.Test;

public class TrustedInputPluginTests
{
    [Test]
    public async Task Capture_preserves_inner_processing_and_external_witness_ownership([Values] bool externalRequest, [Values] bool fail)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        WitnessRendezvous rendezvous = new();
        using WitnessRequest? external = externalRequest ? rendezvous.RequestWitness(block.Hash!) : null;
        IBlockProcessor inner = Substitute.For<IBlockProcessor>();
        inner.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Prague.Instance, default)
            .Returns(_ =>
            {
                Assert.That(rendezvous.HasPendingRequest(block.Hash!), Is.True);
                if (fail) throw new InvalidOperationException();
                return (block, Array.Empty<TxReceipt>());
            });
        TrustedInputStore store = new(new TrustedInputConfig(), LimboLogs.Instance);
        await using TrustedInputCapturingBlockProcessor processor = new(inner, rendezvous, store,
            MainnetSpecProvider.Instance, LimboLogs.Instance);

        Block? processed = null;
        if (fail)
            Assert.Throws<InvalidOperationException>(() => processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Prague.Instance));
        else
            processed = processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Prague.Instance).Block;

        inner.Received(1).ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Prague.Instance, default);
        using (Assert.EnterMultipleScope())
        {
            if (!fail) Assert.That(processed, Is.SameAs(block));
            Assert.That(rendezvous.HasPendingRequest(block.Hash!), Is.EqualTo(externalRequest));
            Assert.That(store.TryGet(block.Hash!, out _), Is.False);
            if (external is not null) Assert.That(external.Task.IsCompleted, Is.False);
        }
    }

    [Test]
    public async Task Busy_capture_processes_without_requesting_a_witness()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        WitnessRendezvous rendezvous = new();
        IBlockProcessor inner = Substitute.For<IBlockProcessor>();
        inner.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Prague.Instance, default)
            .Returns(_ =>
            {
                Assert.That(rendezvous.HasPendingRequest(block.Hash!), Is.False);
                return (block, Array.Empty<TxReceipt>());
            });
        await using TrustedInputCapturingBlockProcessor processor = new(inner, rendezvous,
            new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance), MainnetSpecProvider.Instance, LimboLogs.Instance);
        using ManualResetEventSlim release = new();
        try
        {
            bool scheduled = processor.TryScheduleCapture(() => () => release.Wait());
            Block processed = processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Prague.Instance).Block;
            inner.Received(1).ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Prague.Instance, default);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(scheduled, Is.True);
                Assert.That(processed, Is.SameAs(block));
            }
        }
        finally
        {
            release.Set();
            await processor.DisposeAsync();
        }
    }

    [Test]
    public async Task Rpc_returns_the_block_as_a_trusted_input_and_then_serves_it_from_the_store()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Block block = await chain.AddBlock(Transfer(chain));
        TrustedInputRpcModule module = new(new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance),
            chain.BlockTree, chain.Bridge, chain.SpecProvider);

        ResultWrapper<byte[]> first = module.trustedinput_getInput(new BlockParameter(block.Number));
        ResultWrapper<byte[]> second = module.trustedinput_getInput(new BlockParameter(block.Hash!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Result.ResultType, Is.EqualTo(ResultType.Success), first.Result.Error);
            Assert.That(Keccak.Compute(Input.BlockHeaderOf(first.Data)), Is.EqualTo(block.Hash));
            Assert.That(Input.ChainIdOf(first.Data), Is.EqualTo(chain.SpecProvider.ChainId));
            Assert.That(second.Data, Is.SameAs(first.Data));
        }
    }

    [Test]
    public async Task Rpc_rejects_the_genesis_block()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        TrustedInputRpcModule module = new(new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance),
            chain.BlockTree, chain.Bridge, chain.SpecProvider);

        Assert.That(module.trustedinput_getInput(new BlockParameter(0UL)).Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public async Task Processed_block_is_captured_as_it_is_imported()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder => builder
            .AddSingleton<ITrustedInputConfig>(new TrustedInputConfig { Enabled = true })
            .AddSingleton<WitnessCapturingBlockProcessingEnv>()
            .AddSingleton<WitnessRendezvous>()
            .AddSingleton<IMainProcessingModule>(new WitnessCapturingMainProcessingModule(false))
            .AddModule(new TrustedInputModule()));

        TrustedInputCapturingBlockProcessor processor = (TrustedInputCapturingBlockProcessor)chain.Container.Resolve<IMainProcessingContext>().BlockProcessor;
        int events = 0;
        processor.TransactionsExecuted += _ => events++;
        Block block = await chain.AddBlock(Transfer(chain));
        await processor.DisposeAsync();

        Assert.That(events, Is.EqualTo(1));
        TrustedInputStore store = chain.Container.Resolve<TrustedInputStore>();
        Assert.That(store.TryGet(block.Hash!, out _), Is.True);
        store.TryGet(block.Hash!, out byte[] input);
        Assert.That(Keccak.Compute(Input.BlockHeaderOf(input)), Is.EqualTo(block.Hash));
    }

    [Test]
    public async Task Capture_has_no_backlog_and_shutdown_drains_the_running_capture()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder => builder
            .AddSingleton<ITrustedInputConfig>(new TrustedInputConfig { Enabled = true })
            .AddSingleton<WitnessCapturingBlockProcessingEnv>()
            .AddSingleton<WitnessRendezvous>()
            .AddSingleton<IMainProcessingModule>(new WitnessCapturingMainProcessingModule(false))
            .AddModule(new TrustedInputModule()));
        TrustedInputCapturingBlockProcessor processor = (TrustedInputCapturingBlockProcessor)chain.Container.Resolve<IMainProcessingContext>().BlockProcessor;
        using ManualResetEventSlim release = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Throws<InvalidOperationException>(() => processor.TryScheduleCapture(() => throw new InvalidOperationException()));
            Assert.That(processor.TryScheduleCapture(() => () => { started.SetResult(); release.Wait(); }), Is.True);
            await started.Task;
            Assert.That(processor.TryScheduleCapture(() => throw new AssertionException("Busy capture must not build a witness")), Is.False);
            ValueTask shutdown = processor.DisposeAsync();
            Assert.That(shutdown.IsCompleted, Is.False);
            release.Set();
            await shutdown;
            Assert.That(processor.TryScheduleCapture(() => throw new AssertionException("Stopped capture must not build a witness")), Is.False);
        }
        finally
        {
            release.Set();
            await processor.DisposeAsync();
        }
    }

    private static Transaction Transfer(TestRpcBlockchain chain) =>
        Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1).WithNonce(chain.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithGasLimit(21_000).WithMaxFeePerGas(20.GWei).WithType(TxType.EIP1559)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
}
