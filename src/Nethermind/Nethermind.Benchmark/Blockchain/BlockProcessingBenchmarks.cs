// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Blockchain;

[MemoryDiagnoser]
public class BlockProcessingBenchmarks
{
    private IContainer _container = null!;
    private IBlockchainProcessor _blockchainProcessor = null!;
    private Block _block = null!;

    private readonly ProcessingOptions _processingOptions =
        ProcessingOptions.ForceProcessing |
        ProcessingOptions.NoValidation |
        ProcessingOptions.ReadOnlyChain |
        ProcessingOptions.DoNotUpdateHead;

    [Params(1, 32, 128)]
    public int TransactionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Build();

        IMainProcessingContext mainProcessingContext = _container.Resolve<IMainProcessingContext>();
        _blockchainProcessor = mainProcessingContext.BlockchainProcessor;
        _block = CreateSyntheticGenesisBlock(TransactionCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _blockchainProcessor.StopAsync().GetAwaiter().GetResult();
        _container.Dispose();
    }

    [Benchmark]
    public long ProcessReadOnlySyntheticGenesisBlock()
    {
        BlockchainProcessor processor = (BlockchainProcessor)_blockchainProcessor;
        Block processed = processor.Process(_block, _processingOptions, NullBlockTracer.Instance, default, out string error);
        if (processed is null)
        {
            throw new InvalidOperationException($"Block processing returned null. Error: {error ?? "<none>"}");
        }

        return processed.Number;
    }

    private static Block CreateSyntheticGenesisBlock(int transactionCount)
    {
        Transaction[] transactions = new Transaction[transactionCount];
        for (int i = 0; i < transactionCount; i++)
        {
            transactions[i] = Build.A.Transaction
                .To(TestItem.AddressB)
                .WithNonce((UInt256)i)
                .WithGasPrice(UInt256.Zero)
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithValue(0)
                .WithIsServiceTransaction(true)
                .SignedAndResolved(TestItem.PrivateKeyA, isEip155Enabled: true)
                .TestObject;
        }

        long gasLimit = Math.Max(4_000_000, transactionCount * Transaction.BaseTxGasCost + 500_000);

        return Build.A.Block.Genesis
            .WithGasLimit(gasLimit)
            .WithTotalDifficulty(1L)
            .WithTransactions(transactions)
            .TestObject;
    }
}
