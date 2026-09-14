// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class HistoricalTracePrewarmerTests
{
    private readonly IReadOnlyTxProcessingEnvFactory _factory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
    private readonly IReadOnlyTxProcessorSource _environment = Substitute.For<IReadOnlyTxProcessorSource>();
    private readonly IReadOnlyTxProcessingScope _scope = Substitute.For<IReadOnlyTxProcessingScope>();
    private readonly ITransactionProcessor _processor = Substitute.For<ITransactionProcessor>();
    private readonly TestBlockTree _blockTree = new() { Head = Build.A.Block.WithNumber(1_000).TestObject };
    private readonly BlockHeader _parent = Build.A.BlockHeader.WithNumber(1).TestObject;

    [SetUp]
    public void SetUp()
    {
        _factory.Create().Returns(_environment);
        _environment.Build(_parent).Returns(_scope);
        _scope.TransactionProcessor.Returns(_processor);
    }

    [TestCase(0, 2, true)]
    [TestCase(1, 999, true)]
    [TestCase(1, 1_001, true)]
    [TestCase(1, 2, false)]
    public void Prewarm_IneligibleBlock_DoesNotCreateWorkers(int concurrency, int number, bool canonical)
    {
        _blockTree.Canonical = canonical;
        Create(concurrency).Prewarm(Block(number), _parent, CancellationToken.None);
        _factory.DidNotReceive().Create();
    }

    [Test]
    public void Prewarm_YoungChain_DoesNotUnderflowDistanceCheck()
    {
        _blockTree.Head = Build.A.Block.WithNumber(3).TestObject;
        Create().Prewarm(Block(), _parent, CancellationToken.None);
        _factory.DidNotReceive().Create();
    }

    [TestCase(TxType.Legacy)]
    [TestCase(TxType.AccessList)]
    [TestCase(TxType.EIP1559)]
    public void Prewarm_SupportedTransaction_CopiesTransactionAndBoundsGas(TxType type)
    {
        Block block = Block();
        Transaction source = block.Transactions[0];
        source.Type = type;
        source.GasLimit = 20_000_000;
        Transaction warmed = null;
        _processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Returns(call => { warmed = call.Arg<Transaction>(); return default; });

        Create().Prewarm(block, _parent, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed, Is.Not.Null.And.Not.SameAs(source));
            Assert.That(warmed!.GasLimit, Is.EqualTo(8_000_000));
            Assert.That(source.GasLimit, Is.EqualTo(20_000_000));
        }
        _processor.Received(1).SetBlockExecutionContext(Arg.Is<BlockHeader>(h => !ReferenceEquals(h, block.Header) && h.Number == block.Number));
        _processor.Received(1).Process(Arg.Any<Transaction>(), Arg.Is<ITxTracer>(t => t.IsCancelable && !t.IsTracingInstructions),
            ExecutionOptions.Warmup | ExecutionOptions.SkipValidation);
        _scope.Received(1).Dispose();
        _environment.Received(1).Dispose();
    }

    [TestCase(TxType.Blob)]
    [TestCase(TxType.SetCode)]
    [TestCase(TxType.DepositTx)]
    public void Prewarm_UnsupportedTransaction_DoesNotExecute(TxType type)
    {
        Block block = Block();
        block.Transactions[0].Type = type;
        Create().Prewarm(block, _parent, CancellationToken.None);
        _environment.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Prewarm_AuthorizationList_DoesNotExecute()
    {
        Block block = Block();
        block.Transactions[0].AuthorizationList = [];
        Create().Prewarm(block, _parent, CancellationToken.None);
        _environment.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Prewarm_Failure_DisposesWorkersAndReleasesAdmission()
    {
        _processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Returns(_ => throw new InvalidOperationException("speculative failure"));
        HistoricalTracePrewarmer prewarmer = Create();

        Assert.DoesNotThrow(() => prewarmer.Prewarm(Block(), _parent, CancellationToken.None));
        Assert.DoesNotThrow(() => prewarmer.Prewarm(Block(), _parent, CancellationToken.None));

        _scope.Received(2).Dispose();
        _environment.Received(2).Dispose();
        _factory.Received(2).Create();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Prewarm_Cancellation_DisposesWorkersAndReleasesAdmission(bool throwAfterCancellation)
    {
        using CancellationTokenSource cancellation = new();
        _processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                if (throwAfterCancellation) throw new InvalidOperationException("speculative failure after cancellation");
                return default;
            });
        HistoricalTracePrewarmer prewarmer = Create();

        Assert.Throws<OperationCanceledException>(() => prewarmer.Prewarm(Block(), _parent, cancellation.Token));
        prewarmer.Prewarm(Block(), _parent, CancellationToken.None);

        _scope.Received(2).Dispose();
        _environment.Received(2).Dispose();
    }

    [Test]
    public void Prewarm_ConcurrentRequest_DoesNotCreateMoreWorkers()
    {
        HistoricalTracePrewarmer prewarmer = Create();
        _processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Returns(_ => { prewarmer.Prewarm(Block(), _parent, CancellationToken.None); return default; });

        prewarmer.Prewarm(Block(), _parent, CancellationToken.None);

        _factory.Received(1).Create();
        _scope.Received(1).Dispose();
    }

    private HistoricalTracePrewarmer Create(int concurrency = 1) =>
        new(_factory, _blockTree, new EthereumEcdsa(1), new(concurrency), LimboLogs.Instance);

    private static Block Block(int number = 2) => Build.A.Block.WithNumber(number)
        .WithTransactions(Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject).TestObject;

    private sealed class TestBlockTree : BlockTreeTestDouble
    {
        public bool Canonical { get; set; } = true;
        public override bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => Canonical;
    }
}
