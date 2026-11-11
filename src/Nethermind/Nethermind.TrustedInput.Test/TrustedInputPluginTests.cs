// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stateless.Execution.IO;
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
        inner.ProcessOne(block, ProcessingOptions.None, Arg.Any<IBlockTracer>(), Prague.Instance, default)
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

        inner.Received(1).ProcessOne(block, ProcessingOptions.None, Arg.Any<IBlockTracer>(), Prague.Instance, default);
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
    public async Task Capture_keeps_the_access_list_the_import_frees()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        byte[] blockAccessList = Bytes.FromHexString("0x0123456789abcdeffedcba9876543210");
        WitnessRendezvous rendezvous = new();
        IBlockProcessor inner = Substitute.For<IBlockProcessor>();
        inner.ProcessOne(block, ProcessingOptions.None, Arg.Any<IBlockTracer>(), Prague.Instance, default)
            .Returns(call =>
            {
                IBlockTracer tracer = call.Arg<IBlockTracer>();
                tracer.StartNewBlockTrace(block);
                tracer.StartNewTxTrace(null).ReportAction(3000, default, TestItem.AddressA,
                    ECRecoverPrecompile.Address, RecoveryCall(), ExecutionType.CALL, true);
                tracer.EndTxTrace();
                tracer.EndBlockTrace();
                block.EncodedBlockAccessList = blockAccessList;
                Assert.That(rendezvous.TryClaim(block.Hash!, out TaskCompletionSource<Witness?>? witness), Is.True);
                witness!.SetResult(EmptyWitness());
                return (block, Array.Empty<TxReceipt>());
            });
        TrustedInputStore store = new(new TrustedInputConfig(), LimboLogs.Instance);
        TrustedInputCapturingBlockProcessor processor = new(inner, rendezvous, store, MainnetSpecProvider.Instance, LimboLogs.Instance);

        processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Prague.Instance);
        block.EncodedBlockAccessList = null;
        await processor.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.TryGet(block.Hash!, out byte[] input), Is.True);
            Assert.That(input.AsSpan().IndexOf(blockAccessList), Is.Positive);
            TrustedPayload payload = Input.Read(input);
            using Witness witness = payload.Witness;
            Assert.That(payload.RecoveryHints, Has.Length.EqualTo(Input.RecoveryHintLength));
        }
    }

    [Test]
    public void Store_rejects_a_cache_size_below_one([Values(0, -1)] int cacheSize) =>
        Assert.Throws<InvalidConfigurationException>(() => new TrustedInputStore(new TrustedInputConfig { CacheSize = cacheSize }, LimboLogs.Instance));

    [Test]
    public void Store_replaces_the_file_of_an_input_whole()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TrustedInputStore store = new(new TrustedInputConfig { OutputDirectory = directory }, LimboLogs.Instance);

            store.Add(1, TestItem.KeccakA, [1, 2, 3]);
            store.Add(1, TestItem.KeccakA, [4, 5]);

            string[] files = Directory.GetFiles(directory);
            Assert.That(files, Has.Length.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Path.GetFileName(files[0]), Is.EqualTo($"1-{TestItem.KeccakA.ToString(withZeroX: false)}.nti"));
                Assert.That(File.ReadAllBytes(files[0]), Is.EqualTo(new byte[] { 4, 5 }));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A file that cannot be written leaves the input served from memory and no partial file.</summary>
    [Test]
    public void Store_keeps_an_input_whose_file_cannot_be_written([Values] bool outputDirectoryIsFile)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string outputDirectory = directory;
            if (outputDirectoryIsFile)
            {
                outputDirectory = Path.Combine(directory, "file");
                File.WriteAllBytes(outputDirectory, []);
            }
            else
            {
                // A directory where the file goes fails the move into place.
                Directory.CreateDirectory(Path.Combine(directory, $"1-{TestItem.KeccakA.ToString(withZeroX: false)}.nti"));
            }

            TrustedInputStore store = new(new TrustedInputConfig { OutputDirectory = outputDirectory }, LimboLogs.Instance);
            byte[] input = [1, 2, 3];

            Assert.DoesNotThrow(() => store.Add(1, TestItem.KeccakA, input));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(store.TryGet(TestItem.KeccakA, out byte[] stored), Is.True);
                Assert.That(stored, Is.SameAs(input));
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
    public async Task Rpc_coalesces_concurrent_cache_misses()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Block block = await chain.AddBlock(Transfer(chain));
        using Barrier callers = new(4);
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        finder.FindHeader(block.ParentHash!).Returns(_ =>
        {
            Assert.That(callers.SignalAndWait(TimeSpan.FromSeconds(30)), Is.True);
            return chain.BlockTree.FindHeader(block.ParentHash!);
        });
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GenerateExecutionWitness(Arg.Any<BlockHeader>(), block, Arg.Any<IBlockTracer>())
            .Returns(call => chain.Bridge.GenerateExecutionWitness(call.Arg<BlockHeader>(), block, call.Arg<IBlockTracer>()));
        TrustedInputRpcModule module = new(new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance),
            finder, bridge, chain.SpecProvider);

        ResultWrapper<byte[]>[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(
            () => module.trustedinput_getInput(new BlockParameter(block.Hash!)), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default)));

        bridge.Received(1).GenerateExecutionWitness(Arg.Any<BlockHeader>(), block, Arg.Any<IBlockTracer>());
        foreach (ResultWrapper<byte[]> result in results)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
                Assert.That(result.Data, Is.SameAs(results[0].Data));
            }
        }
    }

    [Test]
    public async Task Rpc_retries_generation_after_failure()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Block block = await chain.AddBlock(Transfer(chain));
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GenerateExecutionWitness(Arg.Any<BlockHeader>(), block, Arg.Any<IBlockTracer>())
            .Returns(_ => throw new InvalidOperationException(),
                call => chain.Bridge.GenerateExecutionWitness(call.Arg<BlockHeader>(), block, call.Arg<IBlockTracer>()));
        TrustedInputRpcModule module = new(new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance),
            chain.BlockTree, bridge, chain.SpecProvider);

        Assert.Throws<InvalidOperationException>(() => module.trustedinput_getInput(new BlockParameter(block.Hash!)));
        ResultWrapper<byte[]> result = module.trustedinput_getInput(new BlockParameter(block.Hash!));

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
        bridge.Received(2).GenerateExecutionWitness(Arg.Any<BlockHeader>(), block, Arg.Any<IBlockTracer>());
    }

    /// <summary>The hints come from the execution that generates the witness, which an empty witness cannot repeat.</summary>
    [Test]
    public void Rpc_takes_the_recovery_hints_from_the_witness_generation()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        finder.FindHeader(block.ParentHash!).Returns(Build.A.BlockHeader.TestObject);
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GenerateExecutionWitness(Arg.Any<BlockHeader>(), block, Arg.Any<IBlockTracer>()).Returns(call =>
        {
            IBlockTracer tracer = call.Arg<IBlockTracer>();
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(null).ReportAction(3000, default, TestItem.AddressA,
                ECRecoverPrecompile.Address, RecoveryCall(), ExecutionType.CALL, true);
            tracer.EndTxTrace();
            tracer.EndBlockTrace();
            return EmptyWitness();
        });
        TrustedInputRpcModule module = new(new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance),
            finder, bridge, MainnetSpecProvider.Instance);

        ResultWrapper<byte[]> result = module.trustedinput_getInput(new BlockParameter(block.Hash!));

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
        TrustedPayload payload = Input.Read(result.Data);
        using Witness witness = payload.Witness;
        Assert.That(payload.RecoveryHints, Has.Length.EqualTo(Input.RecoveryHintLength));
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
    public async Task Processed_block_is_captured_as_it_is_imported([Values] bool witnessCaptureEnabled, [Values] bool recover)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder => builder
            .AddSingleton<ITrustedInputConfig>(new TrustedInputConfig { Enabled = true })
            .AddSingleton<WitnessCapturingBlockProcessingEnv>()
            .AddSingleton<WitnessRendezvous>()
            .AddSingleton<IMainProcessingModule>(new WitnessCapturingMainProcessingModule(witnessCaptureEnabled))
            .AddModule(new TrustedInputModule()));

        TrustedInputCapturingBlockProcessor processor = (TrustedInputCapturingBlockProcessor)chain.Container.Resolve<IMainProcessingContext>().BlockProcessor;
        int events = 0;
        processor.TransactionsExecuted += _ => events++;
        Transaction transaction = recover
            ? Build.A.Transaction.WithTo(ECRecoverPrecompile.Address).WithData(RecoveryCall()).WithGasLimit(50_000)
                .WithGasPrice(20.GWei).WithNonce(chain.ReadOnlyState.GetNonce(TestItem.AddressA))
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject
            : Transfer(chain);
        Block block = await chain.AddBlock(transaction);
        await processor.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(events, Is.EqualTo(1));
            Assert.That(block.Transactions, Has.Length.EqualTo(1));
        }
        TrustedInputStore store = chain.Container.Resolve<TrustedInputStore>();
        Assert.That(store.TryGet(block.Hash!, out _), Is.True);
        store.TryGet(block.Hash!, out byte[] input);
        Assert.That(Keccak.Compute(Input.BlockHeaderOf(input)), Is.EqualTo(block.Hash));
        TrustedPayload payload = Input.Read(input);
        using Witness witness = payload.Witness;
        Assert.That(payload.RecoveryHints, Has.Length.EqualTo(recover ? Input.RecoveryHintLength : 0));
    }

    [Test]
    public async Task Capture_has_no_backlog_and_shutdown_drains_the_running_capture()
    {
        TrustedInputCapturingBlockProcessor processor = new(Substitute.For<IBlockProcessor>(), new WitnessRendezvous(),
            new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance), MainnetSpecProvider.Instance, LimboLogs.Instance);
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

    private static Witness EmptyWitness() => new()
    {
        Codes = ArrayPoolList<byte[]>.Empty(),
        State = ArrayPoolList<byte[]>.Empty(),
        Keys = ArrayPoolList<byte[]>.Empty(),
        Headers = ArrayPoolList<byte[]>.Empty()
    };

    private static byte[] RecoveryCall()
    {
        ValueHash256 message = ValueKeccak.Compute("message"u8);
        Signature signature = new EthereumEcdsa(BlockchainIds.Mainnet).Sign(TestItem.PrivateKeyB, in message);
        byte[] input = new byte[128];
        message.Bytes.CopyTo(input);
        input[63] = (byte)(27 + signature.RecoveryId);
        signature.Bytes.CopyTo(input.AsSpan(64));
        return input;
    }

    private static string CreateTemporaryDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{nameof(TrustedInputPluginTests)}-{Guid.NewGuid():N}")).FullName;

    private static Transaction Transfer(TestRpcBlockchain chain) =>
        Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1).WithNonce(chain.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithGasLimit(21_000).WithGasPrice(20.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
}
