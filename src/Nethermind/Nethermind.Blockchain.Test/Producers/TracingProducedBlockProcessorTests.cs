// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public class TracingProducedBlockProcessorTests
{
    [TestCase(DumpOptions.Receipts, "receipts_{0}.json")]
    [TestCase(DumpOptions.Parity, "parityStyle_{0}.json")]
    [TestCase(DumpOptions.Geth, "gethStyle_{0}.json")]
    [TestCase(DumpOptions.Rlp, "block_{0}.rlp")]
    public void Dumps_trace_of_produced_block_and_keeps_caller_tracer(DumpOptions dumpOptions, string fileNameFormat)
    {
        using TempDirectory dumpDirectory = new();
        Block block = Build.A.Block.WithNumber(1).TestObject;
        IBlockTracer callerTracer = Substitute.For<IBlockTracer>();
        TracingProducedBlockProcessor processor = CreateProcessor(CreateInner(block), dumpOptions, dumpDirectory.Path);

        Block? processed = processor.Process(block, ProcessingOptions.ProducingBlock, callerTracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed, Is.SameAs(block));
            Assert.That(Directory.GetFiles(dumpDirectory.Path).Select(Path.GetFileName),
                Is.EqualTo(new[] { string.Format(fileNameFormat, $"{block.Number}_{block.Hash}") }));
            callerTracer.Received(1).StartNewBlockTrace(block);
            callerTracer.Received(1).EndBlockTrace();
        }
    }

    [Test]
    public void Writes_nothing_when_disabled_or_block_not_produced([Values] bool disabled)
    {
        using TempDirectory dumpDirectory = new();
        Block block = Build.A.Block.WithNumber(1).TestObject;
        IBlockTracer callerTracer = Substitute.For<IBlockTracer>();
        IBlockchainProcessor inner = CreateInner(disabled ? block : null);
        TracingProducedBlockProcessor processor = CreateProcessor(inner, disabled ? DumpOptions.None : DumpOptions.Receipts, dumpDirectory.Path);

        processor.Process(block, ProcessingOptions.ProducingBlock, callerTracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(dumpDirectory.Path), Is.Empty);
            if (disabled) inner.Received(1).Process(block, ProcessingOptions.ProducingBlock, callerTracer, Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public void Keeps_only_newest_dumps([Values(1, 2, 3)] int producedBlocks)
    {
        const int maxDumpFiles = 2;
        using TempDirectory dumpDirectory = new();
        Block[] blocks = Enumerable.Range(1, producedBlocks).Select(static n => Build.A.Block.WithNumber((ulong)n).TestObject).ToArray();
        DateTime writeTime = DateTime.UtcNow.AddHours(-1);
        foreach (Block block in blocks)
        {
            TracingProducedBlockProcessor processor = CreateProcessor(CreateInner(block), DumpOptions.Receipts, dumpDirectory.Path, maxDumpFiles);
            processor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);

            // Backdates each dump in production order, so the order holds whatever the file system timestamp resolution.
            writeTime = writeTime.AddSeconds(1);
            File.SetLastWriteTimeUtc(Path.Combine(dumpDirectory.Path, $"receipts_{block.Number}_{block.Hash}.json"), writeTime);
        }

        Assert.That(Directory.GetFiles(dumpDirectory.Path).Select(Path.GetFileName),
            Is.EquivalentTo(blocks.TakeLast(maxDumpFiles).Select(static b => $"receipts_{b.Number}_{b.Hash}.json")));
    }

    [Test]
    public async Task Producer_environment_dumps_only_included_transactions_tagged_with_produced_block()
    {
        Transaction[] candidates = [];
        ITxSource txSource = Substitute.For<ITxSource>();
        txSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes?>(), Arg.Any<bool>())
            .Returns(_ => candidates);
        IBlockProducerTxSourceFactory txSourceFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        txSourceFactory.Create().Returns(txSource);

        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton(txSourceFactory)
            .AddDecorator<IMiningConfig>(static (_, config) =>
            {
                config.DumpProducedBlocks = DumpOptions.Receipts | DumpOptions.Parity | DumpOptions.Geth;
                return config;
            }));

        Transaction included0 = Transfer(chain, TestItem.PrivateKeyA, GasCostOf.Transaction);
        Transaction belowIntrinsicGas = Transfer(chain, TestItem.PrivateKeyB, GasCostOf.Transaction - 1);
        Transaction included1 = Transfer(chain, TestItem.PrivateKeyC, GasCostOf.Transaction);
        candidates = [included0, belowIntrinsicGas, included1];

        Block block = await chain.AddBlock(TestBlockchainUtil.AddBlockFlags.MayHaveExtraTx);
        Assert.That(block.Transactions.Select(static tx => tx.Hash), Is.EqualTo(new[] { included0.Hash, included1.Hash }), "the candidate below intrinsic gas must fail BuildUp");

        string[] includedHashes = [included0.Hash!.ToString(), included1.Hash!.ToString()];
        string blockHash = block.Hash!.ToString();
        JsonElement receipts = ReadDump("receipts", block);
        JsonElement parity = ReadDump("parityStyle", block);
        JsonElement geth = ReadDump("gethStyle", block);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipts.EnumerateArray().Select(static r => r.GetProperty("txHash").GetString()), Is.EqualTo(includedHashes), "receipt tx hashes");
            Assert.That(receipts.EnumerateArray().Select(static r => r.GetProperty("index").GetInt32()), Is.EqualTo(new[] { 0, 1 }), "receipt indexes");
            Assert.That(receipts.EnumerateArray().Select(static r => r.GetProperty("blockHash").GetString()), Is.All.EqualTo(blockHash), "receipt block hashes");
            Assert.That(parity.EnumerateArray().Select(static t => t.GetProperty("transactionHash").GetString()), Is.EqualTo(includedHashes), "parity tx hashes");
            Assert.That(parity.EnumerateArray().Select(static t => t.GetProperty("transactionPosition").GetInt32()), Is.EqualTo(new[] { 0, 1 }), "parity tx positions");
            Assert.That(parity.EnumerateArray().Select(static t => t.GetProperty("blockHash").GetString()), Is.All.EqualTo(blockHash), "parity block hashes");
            Assert.That(geth.GetArrayLength(), Is.EqualTo(block.Transactions.Length), "geth trace count");
        }
    }

    private static TracingProducedBlockProcessor CreateProcessor(IBlockchainProcessor inner, DumpOptions dumpOptions, string dumpDirectory, int maxDumpFiles = 256) =>
        new(inner, new MiningConfig { DumpProducedBlocks = dumpOptions }, MainnetSpecProvider.Instance, LimboLogs.Instance)
        {
            DumpDirectory = dumpDirectory,
            MaxDumpFiles = maxDumpFiles
        };

    private static IBlockchainProcessor CreateInner(Block? result)
    {
        IBlockchainProcessor inner = Substitute.For<IBlockchainProcessor>();
        inner.Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IBlockTracer tracer = call.Arg<IBlockTracer>();
                tracer.StartNewBlockTrace(call.Arg<Block>());
                tracer.EndBlockTrace();
                return result;
            });
        return inner;
    }

    private static Transaction Transfer(BasicTestBlockchain chain, PrivateKey sender, ulong gasLimit) =>
        Build.A.Transaction
            .WithNonce(chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, sender.Address))
            .WithGasLimit(gasLimit)
            .To(TestItem.AddressD)
            .SignedAndResolved(sender)
            .TestObject;

    private static JsonElement ReadDump(string prefix, Block block)
    {
        string dumpFile = Path.Combine(TracingProducedBlockProcessor.DefaultDumpDirectory, $"{prefix}_{block.Number}_{block.Hash}.json");
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(dumpFile));
            return document.RootElement.Clone();
        }
        finally
        {
            File.Delete(dumpFile);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("produced-blocks-test").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }
}
