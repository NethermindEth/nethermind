// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

using DbMetrics = Nethermind.Db.Metrics;

namespace Nethermind.Consensus.Test.Processing;

public class PrewarmerLocalBenchmarkTests
{
    private const string EnabledEnvVar = "NETHERMIND_LOCAL_PREWARMER_BENCHMARK";
    private const int TransactionCount = 96;
    private const int StorageSlotsPerTransaction = 64;
    private const int WarmupIterations = 3;
    private const int MeasurementIterations = 12;

    [Test]
    [Category("LocalBenchmark")]
    public void Local_prewarmer_strategy_benchmark()
    {
        if (Environment.GetEnvironmentVariable(EnabledEnvVar) != "1")
        {
            Assert.Ignore($"Set {EnabledEnvVar}=1 to run the local prewarmer benchmark.");
        }

        BenchmarkCase[] benchmarkCases =
        [
            CreateDefaultCase("current-default"),
            new("no-prewarm", Enabled: false, Concurrency: 0, FirstPassRatio: 1.0, RetryMode: "None", HeadStartMs: 0),
            new("4t-none", Enabled: true, Concurrency: 4, FirstPassRatio: 1.0, RetryMode: "None", HeadStartMs: 0),
            new("4t-gated", Enabled: true, Concurrency: 4, FirstPassRatio: 1.0, RetryMode: "StateGated", HeadStartMs: 0),
            new("4t-hammer", Enabled: true, Concurrency: 4, FirstPassRatio: 1.0, RetryMode: "Hammer", HeadStartMs: 0),
            new("8t-none", Enabled: true, Concurrency: 8, FirstPassRatio: 1.0, RetryMode: "None", HeadStartMs: 0),
            new("8t-gated", Enabled: true, Concurrency: 8, FirstPassRatio: 1.0, RetryMode: "StateGated", HeadStartMs: 0),
            new("8t-hammer", Enabled: true, Concurrency: 8, FirstPassRatio: 1.0, RetryMode: "Hammer", HeadStartMs: 0),
            new("14t-none", Enabled: true, Concurrency: 14, FirstPassRatio: 1.0, RetryMode: "None", HeadStartMs: 0),
            new("14t-gated", Enabled: true, Concurrency: 14, FirstPassRatio: 1.0, RetryMode: "StateGated", HeadStartMs: 0),
            new("14t-hammer", Enabled: true, Concurrency: 14, FirstPassRatio: 1.0, RetryMode: "Hammer", HeadStartMs: 0),
            new("4t-100-head10", Enabled: true, Concurrency: 4, FirstPassRatio: 1.0, RetryMode: "Hammer", HeadStartMs: 10),
            CreateDefaultCase("current-default-repeat"),
        ];

        using (BenchmarkContext primer = BenchmarkContext.Create(
            CreateDefaultCase("jit-primer"),
            TransactionCount,
            StorageSlotsPerTransaction))
        {
            primer.ProcessBlock();
        }

        StringBuilder report = new();
        WriteReportLine(
            report,
            $"tx={TransactionCount}, storage_slots_per_tx={StorageSlotsPerTransaction}, warmup={WarmupIterations}, measured={MeasurementIterations}");
        WriteReportLine(
            report,
            "case,total_ms,avg_ms,account_hit_rate,storage_hit_rate,account_hits,account_misses,storage_hits,storage_misses");

        BenchmarkRun[] runs = new BenchmarkRun[benchmarkCases.Length];
        try
        {
            for (int i = 0; i < runs.Length; i++)
            {
                runs[i] = new BenchmarkRun(
                    benchmarkCases[i],
                    BenchmarkContext.Create(
                        benchmarkCases[i],
                        TransactionCount,
                        StorageSlotsPerTransaction));
            }

            for (int warmup = 0; warmup < WarmupIterations; warmup++)
            {
                for (int i = 0; i < runs.Length; i++)
                {
                    runs[i].Context.ProcessBlock();
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            for (int iteration = 0; iteration < MeasurementIterations; iteration++)
            {
                for (int i = 0; i < runs.Length; i++)
                {
                    MetricSnapshot before = MetricSnapshot.Capture();
                    Stopwatch stopwatch = Stopwatch.StartNew();

                    runs[i].Context.ProcessBlock();

                    stopwatch.Stop();
                    MetricSnapshot after = MetricSnapshot.Capture();
                    runs[i].Add(stopwatch.Elapsed.TotalMilliseconds, after - before);
                }
            }

            for (int i = 0; i < runs.Length; i++)
            {
                BenchmarkRun run = runs[i];
                MetricDelta delta = run.Delta;
                WriteReportLine(
                    report,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{run.Case.Name},{run.TotalMilliseconds:F2},{run.TotalMilliseconds / MeasurementIterations:F2}," +
                        $"{delta.AccountHitRate:F1},{delta.StorageHitRate:F1},{delta.AccountHits},{delta.AccountReads},{delta.StorageHits},{delta.StorageReads}"));
            }
        }
        finally
        {
            for (int i = 0; i < runs.Length; i++)
            {
                runs[i]?.Dispose();
            }
        }

        string resultPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "prewarmer-local-benchmark.csv");
        File.WriteAllText(resultPath, report.ToString());
        TestContext.AddTestAttachment(resultPath);
        TestContext.Progress.WriteLine($"wrote {resultPath}");
    }

    private static void WriteReportLine(StringBuilder report, string line)
    {
        report.AppendLine(line);
        TestContext.Progress.WriteLine(line);
    }

    private readonly record struct BenchmarkCase(
        string Name,
        bool Enabled,
        int Concurrency,
        double FirstPassRatio,
        string RetryMode,
        int HeadStartMs,
        string FirstPassMode = "SenderGrouped",
        int StorageCacheCapacity = 0,
        bool UseDefaults = false);

    private static BenchmarkCase CreateDefaultCase(string name) =>
        new(name, Enabled: true, Concurrency: 0, FirstPassRatio: 1.0, RetryMode: "", HeadStartMs: 0, FirstPassMode: "", UseDefaults: true);

    private sealed class BenchmarkRun(BenchmarkCase benchmarkCase, BenchmarkContext context) : IDisposable
    {
        public BenchmarkCase Case { get; } = benchmarkCase;

        public BenchmarkContext Context { get; } = context;

        public double TotalMilliseconds { get; private set; }

        public MetricDelta Delta { get; private set; }

        public void Add(double elapsedMilliseconds, MetricDelta delta)
        {
            TotalMilliseconds += elapsedMilliseconds;
            Delta += delta;
        }

        public void Dispose() => Context.Dispose();
    }

    private readonly struct MetricSnapshot
    {
        private readonly long _accountHits;
        private readonly long _accountReads;
        private readonly long _storageHits;
        private readonly long _storageReads;

        private MetricSnapshot(long accountHits, long accountReads, long storageHits, long storageReads)
        {
            _accountHits = accountHits;
            _accountReads = accountReads;
            _storageHits = storageHits;
            _storageReads = storageReads;
        }

        public static MetricSnapshot Capture() => new(
            DbMetrics.MainStateTreeCacheHits,
            DbMetrics.MainStateTreeReads,
            DbMetrics.MainStorageTreeCache,
            DbMetrics.MainStorageTreeReads);

        public static MetricDelta operator -(MetricSnapshot after, MetricSnapshot before) => new(
            after._accountHits - before._accountHits,
            after._accountReads - before._accountReads,
            after._storageHits - before._storageHits,
            after._storageReads - before._storageReads);
    }

    private readonly struct MetricDelta(long accountHits, long accountReads, long storageHits, long storageReads)
    {
        public long AccountHits { get; } = accountHits;

        public long AccountReads { get; } = accountReads;

        public long StorageHits { get; } = storageHits;

        public long StorageReads { get; } = storageReads;

        public double AccountHitRate => HitRate(AccountHits, AccountReads);

        public double StorageHitRate => HitRate(StorageHits, StorageReads);

        public static MetricDelta operator +(MetricDelta left, MetricDelta right) => new(
            left.AccountHits + right.AccountHits,
            left.AccountReads + right.AccountReads,
            left.StorageHits + right.StorageHits,
            left.StorageReads + right.StorageReads);

        private static double HitRate(long hits, long misses)
        {
            long total = hits + misses;
            return total == 0 ? 0.0 : hits * 100.0 / total;
        }
    }

    private sealed class BenchmarkContext : IDisposable
    {
        private const int ContractAddressOffset = 0x1000;
        private static readonly PrivateKey SenderKey = TestItem.PrivateKeyA;
        private static readonly Address SenderAddress = SenderKey.Address;
        private static readonly IReleaseSpec Spec = Osaka.Instance;
        private static readonly byte[] StopCode = [(byte)Instruction.STOP];

        private readonly IContainer _container;
        private readonly ILifetimeScope _processingScope;
        private readonly IBranchProcessor _branchProcessor;
        private readonly BlockHeader _parentHeader;
        private readonly Block _block;

        private BenchmarkContext(
            IContainer container,
            ILifetimeScope processingScope,
            IBranchProcessor branchProcessor,
            BlockHeader parentHeader,
            Block block)
        {
            _container = container;
            _processingScope = processingScope;
            _branchProcessor = branchProcessor;
            _parentHeader = parentHeader;
            _block = block;
        }

        public static BenchmarkContext Create(
            BenchmarkCase benchmarkCase,
            int transactionCount,
            int storageSlotsPerTransaction)
        {
            BlocksConfig blocksConfig = benchmarkCase.UseDefaults
                ? new BlocksConfig()
                : new BlocksConfig
                {
                    PreWarmStateOnBlockProcessing = benchmarkCase.Enabled,
                    PreWarmStateConcurrency = benchmarkCase.Concurrency,
                    PreWarmFirstPassRatio = benchmarkCase.FirstPassRatio,
                    PreWarmRetryMode = benchmarkCase.RetryMode,
                    PreWarmFirstPassMode = benchmarkCase.FirstPassMode,
                    PreWarmHeadStartMs = benchmarkCase.HeadStartMs,
                    SlowBlockThresholdMs = -1,
                    SlowBlockPerTxThresholdMs = -1,
                };

            ContainerBuilder builder = new();
            builder.AddModule(new TestNethermindModule(blocksConfig));

            if (!benchmarkCase.UseDefaults)
            {
                builder.AddDecorator<IBlocksConfig>((_, config) =>
                {
                    config.PreWarmStateOnBlockProcessing = benchmarkCase.Enabled;
                    config.PreWarmStateConcurrency = benchmarkCase.Concurrency;
                    config.PreWarmFirstPassRatio = benchmarkCase.FirstPassRatio;
                    config.PreWarmRetryMode = benchmarkCase.RetryMode;
                    config.PreWarmFirstPassMode = benchmarkCase.FirstPassMode;
                    config.PreWarmHeadStartMs = benchmarkCase.HeadStartMs;
                    return config;
                });
            }

            IContainer container = builder.Build();
            IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();
            IWorldStateScopeProvider scopeProvider = worldStateManager.GlobalWorldState;
            IBlockValidationModule[] validationModules = container.Resolve<IBlockValidationModule[]>();
            IMainProcessingModule[] mainModules = container.Resolve<IMainProcessingModule[]>();
            ILifetimeScope processingScope = container.BeginLifetimeScope(scopeBuilder =>
            {
                scopeBuilder.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>();
                scopeBuilder.RegisterInstance(worldStateManager).As<IWorldStateManager>();
                scopeBuilder.AddModule(validationModules);
                scopeBuilder.AddModule(mainModules);
                if (benchmarkCase.StorageCacheCapacity > 0)
                {
                    scopeBuilder.RegisterInstance(new PreBlockCaches(benchmarkCase.StorageCacheCapacity));
                }
            });

            IWorldState worldState = processingScope.Resolve<IWorldState>();
            BlockHeader parentHeader;
            using (worldState.BeginScope(IWorldState.PreGenesis))
            {
                SeedState(worldState, transactionCount, storageSlotsPerTransaction);

                parentHeader = Build.A.BlockHeader
                    .WithNumber(0)
                    .WithGasLimit(100_000_000)
                    .WithBaseFee(1.GWei)
                    .WithTimestamp(0)
                    .WithStateRoot(worldState.StateRoot)
                    .TestObject;
            }

            Transaction[] transactions = BuildTransactions(transactionCount);
            Block block = Build.A.Block
                .WithParent(parentHeader)
                .WithTransactions(transactions)
                .WithGasLimit(100_000_000)
                .WithBaseFeePerGas(1.GWei)
                .WithTimestamp(1)
                .TestObject;

            IBranchProcessor branchProcessor = processingScope.Resolve<IBranchProcessor>();

            return new BenchmarkContext(container, processingScope, branchProcessor, parentHeader, block);
        }

        public void ProcessBlock()
        {
            CompositeBlockTracer tracer = new();
            bool previousIsBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
            ProcessingThread.IsBlockProcessingThread = true;
            try
            {
                Block[] blocks = _branchProcessor.Process(
                    _parentHeader,
                    [_block],
                    ProcessingOptions.NoValidation,
                    tracer);

                blocks.Should().HaveCount(1);
            }
            finally
            {
                ProcessingThread.IsBlockProcessingThread = previousIsBlockProcessingThread;
            }
        }

        public void Dispose()
        {
            _processingScope.Dispose();
            _container.Dispose();
        }

        private static void SeedState(IWorldState worldState, int transactionCount, int storageSlotsPerTransaction)
        {
            byte[] contractCode = BuildStorageReaderCode(storageSlotsPerTransaction);

            worldState.CreateAccount(SenderAddress, 10_000_000.Ether);

            for (int contractIndex = 0; contractIndex < transactionCount; contractIndex++)
            {
                Address contractAddress = GetContractAddress(contractIndex);
                worldState.CreateAccount(contractAddress, UInt256.Zero);
                worldState.InsertCode(contractAddress, contractCode, Spec);

                for (int storageIndex = 0; storageIndex < storageSlotsPerTransaction; storageIndex++)
                {
                    worldState.Set(
                        new StorageCell(contractAddress, (UInt256)storageIndex),
                        ((UInt256)(storageIndex + 1)).ToBigEndian());
                }
            }

            worldState.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            worldState.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, Spec);
            worldState.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            worldState.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, Spec);

            worldState.Commit(Spec);
            worldState.CommitTree(0);
        }

        private static byte[] BuildStorageReaderCode(int storageSlotsPerTransaction)
        {
            Prepare code = Prepare.EvmCode;

            for (int i = 0; i < storageSlotsPerTransaction; i++)
            {
                code = code
                    .PushData(i)
                    .Op(Instruction.SLOAD)
                    .Op(Instruction.POP);
            }

            return code
                .Op(Instruction.STOP)
                .Done;
        }

        private static Transaction[] BuildTransactions(int transactionCount)
        {
            Transaction[] transactions = new Transaction[transactionCount];

            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithNonce((UInt256)i)
                    .WithTo(GetContractAddress(i))
                    .WithGasLimit(1_000_000)
                    .WithGasPrice(2.GWei)
                    .SignedAndResolved(SenderKey)
                    .TestObject;
            }

            return transactions;
        }

        private static Address GetContractAddress(int index) => Address.FromNumber((UInt256)(ContractAddressOffset + index));
    }
}
