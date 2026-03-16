// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay payload files through NewPayloadHandler flow to mirror
/// production newPayload processing (request decode + payload validation + handler + queue processing).
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasNewPayloadBenchmarks : ITxExecutionTimingCollector
{
    internal const string TimingFileEnvVar = "NETHERMIND_NEWPAYLOAD_REAL_TIMING_FILE";
    internal const string TimingReportFileEnvVar = "NETHERMIND_NEWPAYLOAD_REAL_TIMING_REPORT_FILE";
    private const int MaxConsoleScenarioCount = 10;
    private static readonly JsonSerializerOptions s_jsonOptions = EthereumJsonSerializer.JsonOptions;
    private static readonly object s_timingFileLock = new();

    private IReleaseSpec _releaseSpec;
    private ISpecProvider _specProvider;
    private ILifetimeScope _scope;
    private ILifetimeScope _newPayloadScope;
    private IDisposable _containerLifetime;
    private IWorldState _state;
    private IBranchProcessor _branchProcessor;
    private BlockHeader _preBlockHeader;
    private byte[] _rawJsonBytes;
    private BenchmarkProcessingQueue _processingQueue;
    private NewPayloadHandler _newPayloadHandler;
    private TimedTransactionProcessor _timedTransactionProcessor;
    private IBlockCachePreWarmer _preWarmer;

    private long _jsonParseTicks;
    private long _payloadDeserializeTicks;
    private long _optionalParamsTicks;
    private long _validateForkTicks;
    private long _validateParamsTicks;
    private long _handlerTicks;
    private long _queueTotalTicks;
    private long _senderRecoveryTicks;
    private long _blockProcessTicks;
    private long _txExecutionTicks;
    private int _iterationCount;
    private int _txExecutionCount;
    private readonly long[] _senderRecoveryByTypeTicks = new long[256];
    private readonly int[] _senderRecoveryByTypeCount = new int[256];
    private readonly long[] _txExecutionByTypeTicks = new long[256];
    private readonly int[] _txExecutionByTypeCount = new int[256];

    [ParamsSource(nameof(GetTestCases))]
    public GasPayloadBenchmarks.TestCase Scenario { get; set; }

    public static IEnumerable<GasPayloadBenchmarks.TestCase> GetTestCases() => GasPayloadBenchmarks.GetTestCases();

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Pin to cores 2-5 (avoid core 0 which handles most IRQs on Linux)
        // and elevate priority to minimize OS scheduling noise.
        try
        {
            Process process = Process.GetCurrentProcess();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                process.ProcessorAffinity = new IntPtr(0x3C); // cores 2,3,4,5
            }
            process.PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
            // Best-effort: CI runners may not allow priority changes.
        }

        _releaseSpec = Prague.Instance;
        _specProvider = new SingleReleaseSpecProvider(_releaseSpec, 1, 1);

        GasNewPayloadBenchmarks owner = this;
        (_scope, _preWarmer, _containerLifetime) = BenchmarkContainer.CreateBlockProcessingScope(
            _specProvider,
            GasPayloadBenchmarks.s_genesisPath,
            _releaseSpec,
            additionalRegistrations: childBuilder =>
            {
                childBuilder.AddDecorator<ITransactionProcessor>((_, inner) =>
                    new TimedTransactionProcessor(inner, owner));
            });
        _state = _scope.Resolve<IWorldState>();
        _preBlockHeader = BlockBenchmarkHelper.CreateGenesisHeader();
        _preBlockHeader.TotalDifficulty = UInt256.Zero;
        _timedTransactionProcessor = (TimedTransactionProcessor)_scope.Resolve<ITransactionProcessor>();

        BlockBenchmarkHelper.ExecuteSetupPayload(_state, _timedTransactionProcessor, _preBlockHeader, Scenario, _specProvider);

        _branchProcessor = _scope.Resolve<IBranchProcessor>();

        string rawJson = PayloadLoader.ReadRawJson(Scenario.FilePath);
        _rawJsonBytes = Encoding.UTF8.GetBytes(rawJson);
        ExecutionPayloadParams<ExecutionPayloadV3> initialRequest = DeserializeRequest(collectTiming: false);

        _preBlockHeader.Hash = initialRequest.ExecutionPayload.ParentHash;
        long parentNumber = (long)initialRequest.ExecutionPayload.BlockNumber - 1;
        _preBlockHeader.Number = parentNumber >= 0 ? parentNumber : 0;

        RecoverSignatures recoverSignatures = _scope.Resolve<RecoverSignatures>();
        _processingQueue = new BenchmarkProcessingQueue(
            _branchProcessor,
            _preBlockHeader,
            recoverSignatures,
            this);

        // Create a child scope with runtime-dependent overrides and resolve NewPayloadHandler from DI.
        // Autofac auto-wires all 14 constructor parameters from registered services.
        // If NewPayloadHandler's constructor changes, the benchmark adapts automatically.
        _newPayloadScope = _scope.BeginLifetimeScope(childBuilder =>
        {
            childBuilder
                .AddSingleton<IBlockTree>(new BenchmarkSingleParentBlockTree(_preBlockHeader, isBetterThanHead: false))
                .AddSingleton<IBeaconPivot>(new BenchmarkMergeStubs.StaticBeaconPivot(_preBlockHeader))
                .AddSingleton<IBlockProcessingQueue>(_processingQueue)
                .AddSingleton<NewPayloadHandler>();
        });
        _newPayloadHandler = _newPayloadScope.Resolve<NewPayloadHandler>();

        ExecuteNewPayload(initialRequest, collectTiming: false);

        Block processedBlock = _processingQueue.LastProcessedBlock;
        if (processedBlock is null)
        {
            throw new InvalidOperationException("Warmup did not produce a processed block.");
        }

        PayloadLoader.VerifyProcessedBlock(processedBlock, Scenario.ToString(), Scenario.FilePath);
        ResetTimingAccumulators();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Force full GC to prevent collections during the ~3s measurement.
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    [Benchmark]
    public void ProcessBlock()
    {
        ExecutionPayloadParams<ExecutionPayloadV3> request = DeserializeRequest(collectTiming: true);
        ExecuteNewPayload(request, collectTiming: true);
        _iterationCount++;
    }

    private void ExecuteNewPayload(ExecutionPayloadParams<ExecutionPayloadV3> request, bool collectTiming)
    {
        ExecutionPayload executionPayload = request.ExecutionPayload;
        long start = Stopwatch.GetTimestamp();
        if (!executionPayload.ValidateFork(_specProvider))
        {
            throw new InvalidOperationException("Payload fork validation failed.");
        }
        if (collectTiming)
        {
            _validateForkTicks += Stopwatch.GetTimestamp() - start;
        }

        start = Stopwatch.GetTimestamp();
        IReleaseSpec releaseSpec = _specProvider.GetSpec(executionPayload.BlockNumber, executionPayload.Timestamp);
        Nethermind.Merge.Plugin.Data.ValidationResult validationResult = request.ValidateParams(releaseSpec, EngineApiVersions.NewPayload.V4, out string validationError);
        if (validationResult != Nethermind.Merge.Plugin.Data.ValidationResult.Success)
        {
            throw new InvalidOperationException($"Payload parameter validation failed: {validationError}");
        }
        if (collectTiming)
        {
            _validateParamsTicks += Stopwatch.GetTimestamp() - start;
        }

        _processingQueue.CollectTiming = collectTiming;
        start = Stopwatch.GetTimestamp();
        ResultWrapper<PayloadStatusV1> result = _newPayloadHandler.HandleAsync(executionPayload).GetAwaiter().GetResult();
        if (collectTiming)
        {
            _handlerTicks += Stopwatch.GetTimestamp() - start;
        }
        if (result.ErrorCode != ErrorCodes.None)
        {
            throw new InvalidOperationException($"NewPayload handler returned error code {result.ErrorCode} ({result.Result.Error})");
        }

        if (result.Data is null || result.Data.Status != PayloadStatus.Valid)
        {
            string status = result.Data is null ? "<null>" : result.Data.Status;
            string error = result.Data is null ? "" : result.Data.ValidationError;
            throw new InvalidOperationException($"NewPayload handler returned status {status}. {error}");
        }
    }

    private ExecutionPayloadParams<ExecutionPayloadV3> DeserializeRequest(bool collectTiming)
    {
        long start = Stopwatch.GetTimestamp();
        using JsonDocument doc = JsonDocument.Parse(_rawJsonBytes);
        JsonElement paramsArray = doc.RootElement.GetProperty("params");
        if (collectTiming)
        {
            _jsonParseTicks += Stopwatch.GetTimestamp() - start;
        }

        start = Stopwatch.GetTimestamp();
        ExecutionPayloadV3 payload = JsonSerializer.Deserialize<ExecutionPayloadV3>(
            paramsArray[0].GetRawText(),
            s_jsonOptions);
        if (collectTiming)
        {
            _payloadDeserializeTicks += Stopwatch.GetTimestamp() - start;
        }

        start = Stopwatch.GetTimestamp();
        byte[][] blobVersionedHashes = Array.Empty<byte[]>();
        if (paramsArray.GetArrayLength() > 1 && paramsArray[1].ValueKind == JsonValueKind.Array)
        {
            blobVersionedHashes = JsonSerializer.Deserialize<byte[][]>(
                paramsArray[1].GetRawText(),
                s_jsonOptions);
            if (blobVersionedHashes is null)
            {
                blobVersionedHashes = Array.Empty<byte[]>();
            }
        }

        Hash256 parentBeaconBlockRoot = null;
        if (paramsArray.GetArrayLength() > 2 && paramsArray[2].ValueKind == JsonValueKind.String)
        {
            parentBeaconBlockRoot = JsonSerializer.Deserialize<Hash256>(
                paramsArray[2].GetRawText(),
                s_jsonOptions);
            payload.ParentBeaconBlockRoot = parentBeaconBlockRoot;
        }

        byte[][] executionRequests = null;
        if (paramsArray.GetArrayLength() > 3 && paramsArray[3].ValueKind == JsonValueKind.Array)
        {
            executionRequests = JsonSerializer.Deserialize<byte[][]>(
                paramsArray[3].GetRawText(),
                s_jsonOptions);
            payload.ExecutionRequests = executionRequests;
        }
        if (collectTiming)
        {
            _optionalParamsTicks += Stopwatch.GetTimestamp() - start;
        }

        return new ExecutionPayloadParams<ExecutionPayloadV3>(
            payload,
            blobVersionedHashes,
            parentBeaconBlockRoot,
            executionRequests);
    }

    internal void AddQueueTiming(Block block, long senderRecoveryTicks, long blockProcessTicks, long queueTotalTicks)
    {
        _senderRecoveryTicks += senderRecoveryTicks;
        _blockProcessTicks += blockProcessTicks;
        _queueTotalTicks += queueTotalTicks;
        TimingReportHelper.AddSenderRecoveryTypeBreakdown(block, senderRecoveryTicks, _senderRecoveryByTypeTicks, _senderRecoveryByTypeCount);
    }

    public void AddTxExecutionTiming(TxType txType, long elapsedTicks)
    {
        _txExecutionTicks += elapsedTicks;
        _txExecutionCount++;
        int txTypeIndex = (int)txType;
        _txExecutionByTypeTicks[txTypeIndex] += elapsedTicks;
        _txExecutionByTypeCount[txTypeIndex]++;
    }

    private void ResetTimingAccumulators()
    {
        _jsonParseTicks = 0;
        _payloadDeserializeTicks = 0;
        _optionalParamsTicks = 0;
        _validateForkTicks = 0;
        _validateParamsTicks = 0;
        _handlerTicks = 0;
        _queueTotalTicks = 0;
        _senderRecoveryTicks = 0;
        _blockProcessTicks = 0;
        _txExecutionTicks = 0;
        _iterationCount = 0;
        _txExecutionCount = 0;
        Array.Clear(_senderRecoveryByTypeTicks, 0, _senderRecoveryByTypeTicks.Length);
        Array.Clear(_senderRecoveryByTypeCount, 0, _senderRecoveryByTypeCount.Length);
        Array.Clear(_txExecutionByTypeTicks, 0, _txExecutionByTypeTicks.Length);
        Array.Clear(_txExecutionByTypeCount, 0, _txExecutionByTypeCount.Length);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_iterationCount > 0)
        {
            TimingBreakdownSummary summary = new(
                Scenario?.ToString() ?? "<unknown>",
                _iterationCount,
                _jsonParseTicks,
                _payloadDeserializeTicks,
                _optionalParamsTicks,
                _validateForkTicks,
                _validateParamsTicks,
                _handlerTicks,
                _queueTotalTicks,
                _senderRecoveryTicks,
                _blockProcessTicks,
                _txExecutionTicks,
                _txExecutionCount,
                (long[])_senderRecoveryByTypeTicks.Clone(),
                (int[])_senderRecoveryByTypeCount.Clone(),
                (long[])_txExecutionByTypeTicks.Clone(),
                (int[])_txExecutionByTypeCount.Clone());
            AppendSummaryToFile(summary);
        }

        // Post-benchmark correctness verification: process one more payload and verify result.
        if (_newPayloadHandler is not null && _rawJsonBytes is not null && _processingQueue is not null)
        {
            ExecutionPayloadParams<ExecutionPayloadV3> verificationRequest = DeserializeRequest(collectTiming: false);
            ExecuteNewPayload(verificationRequest, collectTiming: false);
            Block verificationBlock = _processingQueue.LastProcessedBlock;
            if (verificationBlock is null)
            {
                throw new InvalidOperationException(
                    $"Post-benchmark verification failed for {Scenario}: no processed block.");
            }

            PayloadLoader.VerifyProcessedBlock(verificationBlock, Scenario.ToString(), Scenario.FilePath);
        }

        _newPayloadHandler?.Dispose();
        _preWarmer?.ClearCaches();
        _newPayloadScope?.Dispose();
        _scope?.Dispose();
        _containerLifetime?.Dispose();
        _newPayloadHandler = null;
        _processingQueue = null;
        _rawJsonBytes = null;
        _branchProcessor = null;
        _state = null;
        _newPayloadScope = null;
        _scope = null;
        _containerLifetime = null;
        _timedTransactionProcessor = null;
        _preWarmer = null;
    }

    public static void PrintFinalTimingBreakdown()
    {
        string timingFilePath = Environment.GetEnvironmentVariable(TimingFileEnvVar);
        if (string.IsNullOrWhiteSpace(timingFilePath) || !File.Exists(timingFilePath))
        {
            return;
        }

        string[] lines = File.ReadAllLines(timingFilePath);
        List<TimingBreakdownSummary> summaries = new(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            TimingBreakdownSummary summary = JsonSerializer.Deserialize<TimingBreakdownSummary>(line, s_jsonOptions);
            if (summary is not null)
            {
                summaries.Add(summary);
            }
        }

        if (summaries.Count == 0)
        {
            return;
        }

        File.Delete(timingFilePath);

        string reportFilePath = ResolveTimingReportFilePath();
        string reportDirectory = Path.GetDirectoryName(reportFilePath);
        if (!string.IsNullOrWhiteSpace(reportDirectory))
        {
            Directory.CreateDirectory(reportDirectory);
        }

        using (StreamWriter reportWriter = new(reportFilePath, false))
        {
            WriteReport(reportWriter, summaries);
        }

        if (summaries.Count <= MaxConsoleScenarioCount)
        {
            Console.WriteLine();
            WriteReport(Console.Out, summaries);
            Console.WriteLine($"NewPayload timing breakdown saved to: {reportFilePath}");
        }
        else
        {
            Console.WriteLine($"NewPayload timing breakdown saved to: {reportFilePath} (scenario count: {summaries.Count}, console output suppressed for runs with more than {MaxConsoleScenarioCount} scenarios)");
        }
    }

    private static void WriteReport(TextWriter writer, List<TimingBreakdownSummary> summaries)
    {
        writer.WriteLine("=== NewPayload timing breakdown (final) ===");
        long totalJsonParseTicks = 0;
        long totalPayloadDeserializeTicks = 0;
        long totalOptionalParamsTicks = 0;
        long totalValidateForkTicks = 0;
        long totalValidateParamsTicks = 0;
        long totalHandlerTicks = 0;
        long totalQueueTotalTicks = 0;
        long totalSenderRecoveryTicks = 0;
        long totalBlockProcessTicks = 0;
        long totalTxExecutionTicks = 0;
        int totalIterations = 0;
        int totalTxExecutionCount = 0;
        long[] totalSenderRecoveryByTypeTicks = new long[256];
        int[] totalSenderRecoveryByTypeCount = new int[256];
        long[] totalTxExecutionByTypeTicks = new long[256];
        int[] totalTxExecutionByTypeCount = new int[256];

        for (int i = 0; i < summaries.Count; i++)
        {
            TimingBreakdownSummary summary = summaries[i];
            PrintSummary(writer, summary);

            totalJsonParseTicks += summary.JsonParseTicks;
            totalPayloadDeserializeTicks += summary.PayloadDeserializeTicks;
            totalOptionalParamsTicks += summary.OptionalParamsTicks;
            totalValidateForkTicks += summary.ValidateForkTicks;
            totalValidateParamsTicks += summary.ValidateParamsTicks;
            totalHandlerTicks += summary.HandlerTicks;
            totalQueueTotalTicks += summary.QueueTotalTicks;
            totalSenderRecoveryTicks += summary.SenderRecoveryTicks;
            totalBlockProcessTicks += summary.BlockProcessTicks;
            totalTxExecutionTicks += summary.TxExecutionTicks;
            totalIterations += summary.Iterations;
            totalTxExecutionCount += summary.TxExecutionCount;
            TimingReportHelper.AddTypeBreakdown(totalSenderRecoveryByTypeTicks, summary.SenderRecoveryByTypeTicks);
            TimingReportHelper.AddTypeBreakdown(totalSenderRecoveryByTypeCount, summary.SenderRecoveryByTypeCount);
            TimingReportHelper.AddTypeBreakdown(totalTxExecutionByTypeTicks, summary.TxExecutionByTypeTicks);
            TimingReportHelper.AddTypeBreakdown(totalTxExecutionByTypeCount, summary.TxExecutionByTypeCount);
        }

        if (summaries.Count > 1 && totalIterations > 0)
        {
            PrintSummary(writer, new TimingBreakdownSummary(
                "ALL SCENARIOS", totalIterations, totalJsonParseTicks, totalPayloadDeserializeTicks,
                totalOptionalParamsTicks, totalValidateForkTicks, totalValidateParamsTicks,
                totalHandlerTicks, totalQueueTotalTicks, totalSenderRecoveryTicks,
                totalBlockProcessTicks, totalTxExecutionTicks, totalTxExecutionCount,
                totalSenderRecoveryByTypeTicks, totalSenderRecoveryByTypeCount,
                totalTxExecutionByTypeTicks, totalTxExecutionByTypeCount));
        }
    }

    private static string ResolveTimingReportFilePath()
    {
        string configuredPath = Environment.GetEnvironmentVariable(TimingReportFileEnvVar);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(
            Path.GetTempPath(),
            $"nethermind-newpayload-real-timing-breakdown-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
    }

    private static void AppendSummaryToFile(TimingBreakdownSummary summary)
    {
        string timingFilePath = Environment.GetEnvironmentVariable(TimingFileEnvVar);
        if (string.IsNullOrWhiteSpace(timingFilePath))
        {
            return;
        }

        string line = JsonSerializer.Serialize(summary, s_jsonOptions);
        lock (s_timingFileLock)
        {
            File.AppendAllText(timingFilePath, line + Environment.NewLine);
        }
    }

    private static void PrintSummary(TextWriter writer, TimingBreakdownSummary s)
    {
        double jsonParseMs = TimingReportHelper.TicksToMs(s.JsonParseTicks);
        double payloadDeserializeMs = TimingReportHelper.TicksToMs(s.PayloadDeserializeTicks);
        double optionalParamsMs = TimingReportHelper.TicksToMs(s.OptionalParamsTicks);
        double validateForkMs = TimingReportHelper.TicksToMs(s.ValidateForkTicks);
        double validateParamsMs = TimingReportHelper.TicksToMs(s.ValidateParamsTicks);
        double handlerMs = TimingReportHelper.TicksToMs(s.HandlerTicks);
        double queueTotalMs = TimingReportHelper.TicksToMs(s.QueueTotalTicks);
        double senderRecoveryMs = TimingReportHelper.TicksToMs(s.SenderRecoveryTicks);
        double blockProcessMs = TimingReportHelper.TicksToMs(s.BlockProcessTicks);
        double txExecutionMs = TimingReportHelper.TicksToMs(s.TxExecutionTicks);
        double handlerNonQueueMs = handlerMs - queueTotalMs;
        if (handlerNonQueueMs < 0)
        {
            handlerNonQueueMs = 0;
        }

        double queueNonStagedMs = queueTotalMs - senderRecoveryMs - blockProcessMs;
        if (queueNonStagedMs < 0)
        {
            queueNonStagedMs = 0;
        }

        double nonTxBlockMs = blockProcessMs - txExecutionMs;
        if (nonTxBlockMs < 0)
        {
            nonTxBlockMs = 0;
        }

        int senderRecoveryCount = TimingReportHelper.GetTotalCount(s.SenderRecoveryByTypeCount);
        double totalMs = jsonParseMs + payloadDeserializeMs + optionalParamsMs + validateForkMs + validateParamsMs + handlerMs;

        writer.WriteLine($"--- {s.Name} ({s.Iterations} iterations) ---");
        TimingReportHelper.PrintLine(writer, "JSON parse", jsonParseMs, totalMs, s.Iterations);
        TimingReportHelper.PrintLine(writer, "Payload deserialize", payloadDeserializeMs, totalMs, s.Iterations);
        TimingReportHelper.PrintLine(writer, "Optional params", optionalParamsMs, totalMs, s.Iterations);
        TimingReportHelper.PrintLine(writer, "Validate fork", validateForkMs, totalMs, s.Iterations);
        TimingReportHelper.PrintLine(writer, "Validate params", validateParamsMs, totalMs, s.Iterations);
        TimingReportHelper.PrintLine(writer, "Handler total", handlerMs, totalMs, s.Iterations);
        writer.WriteLine($"  {"Total",-20} {totalMs,9:F3} ms                 avg {totalMs / s.Iterations:F3} ms/iter");
        writer.WriteLine("  Handler detail:");
        writer.WriteLine($"    {"Queue total",-18} {queueTotalMs,9:F3} ms  ({TimingReportHelper.GetShare(queueTotalMs, handlerMs),5:F1}% of handler)");
        writer.WriteLine($"    {"Handler non-queue",-18} {handlerNonQueueMs,9:F3} ms  ({TimingReportHelper.GetShare(handlerNonQueueMs, handlerMs),5:F1}% of handler)");
        writer.WriteLine("  Queue detail:");
        writer.WriteLine($"    {"Sender recovery",-18} {senderRecoveryMs,9:F3} ms  ({TimingReportHelper.GetShare(senderRecoveryMs, queueTotalMs),5:F1}% of queue)  avg {TimingReportHelper.GetAverage(senderRecoveryMs, senderRecoveryCount):F3} ms/tx");
        writer.WriteLine($"    {"Block processing",-18} {blockProcessMs,9:F3} ms  ({TimingReportHelper.GetShare(blockProcessMs, queueTotalMs),5:F1}% of queue)");
        writer.WriteLine($"    {"Queue non-staged",-18} {queueNonStagedMs,9:F3} ms  ({TimingReportHelper.GetShare(queueNonStagedMs, queueTotalMs),5:F1}% of queue)");
        writer.WriteLine("  Block processing detail:");
        writer.WriteLine($"    {"Tx execution",-18} {txExecutionMs,9:F3} ms  ({TimingReportHelper.GetShare(txExecutionMs, blockProcessMs),5:F1}% of block)  avg {TimingReportHelper.GetAverage(txExecutionMs, s.TxExecutionCount):F3} ms/tx");
        writer.WriteLine($"    {"Non-tx overhead",-18} {nonTxBlockMs,9:F3} ms  ({TimingReportHelper.GetShare(nonTxBlockMs, blockProcessMs),5:F1}% of block)");
        TimingReportHelper.PrintTxTypeBreakdown(writer, "Tx execution by type", s.TxExecutionByTypeTicks, s.TxExecutionByTypeCount, txExecutionMs);
        writer.WriteLine("  Sender recovery detail:");
        writer.WriteLine($"    {"Recovered txs",-18} {senderRecoveryCount,9} tx");
        writer.WriteLine($"    {"Avg per tx",-18} {TimingReportHelper.GetAverage(senderRecoveryMs, senderRecoveryCount),9:F3} ms/tx");
        TimingReportHelper.PrintTxTypeBreakdown(writer, "Sender recovery by type", s.SenderRecoveryByTypeTicks, s.SenderRecoveryByTypeCount, senderRecoveryMs);
    }

    /// <summary>
    /// Synchronous block processing queue for NewPayload benchmarks.
    /// Implements <see cref="IBlockProcessingQueue"/> so that <see cref="NewPayloadHandler"/>
    /// (resolved from DI) can enqueue blocks the same way it does in production.
    /// Collects per-stage timing breakdowns when <see cref="CollectTiming"/> is set.
    /// </summary>
    internal sealed class BenchmarkProcessingQueue : IBlockProcessingQueue
    {
        private readonly IBranchProcessor _branchProcessor;
        private readonly BlockHeader _parentHeader;
        private readonly RecoverSignatures _recoverSignatures;
        private readonly GasNewPayloadBenchmarks _owner;
        private int _count;

        public BenchmarkProcessingQueue(
            IBranchProcessor branchProcessor,
            BlockHeader parentHeader,
            RecoverSignatures recoverSignatures,
            GasNewPayloadBenchmarks owner)
        {
            _branchProcessor = branchProcessor;
            _parentHeader = parentHeader;
            _recoverSignatures = recoverSignatures;
            _owner = owner;
        }

        public bool CollectTiming { get; set; }

        public Block LastProcessedBlock { get; private set; }

        public event EventHandler ProcessingQueueEmpty;
        public event EventHandler<BlockEventArgs> BlockAdded;
        public event EventHandler<BlockRemovedEventArgs> BlockRemoved;

        public int Count => _count;

        public ValueTask Enqueue(Block block, ProcessingOptions processingOptions)
        {
            Interlocked.Increment(ref _count);
            long queueStart = 0;
            long senderRecoveryTicks = 0;
            long blockProcessTicks = 0;
            try
            {
                BlockAdded?.Invoke(this, new BlockEventArgs(block));
                if (CollectTiming)
                {
                    queueStart = Stopwatch.GetTimestamp();
                    long senderStart = Stopwatch.GetTimestamp();
                    _recoverSignatures.RecoverData(block);
                    senderRecoveryTicks = Stopwatch.GetTimestamp() - senderStart;

                    long processStart = Stopwatch.GetTimestamp();
                    Block[] processedWithTiming = _branchProcessor.Process(
                        _parentHeader,
                        [block],
                        processingOptions,
                        NullBlockTracer.Instance);
                    blockProcessTicks = Stopwatch.GetTimestamp() - processStart;
                    LastProcessedBlock = processedWithTiming[0];
                }
                else
                {
                    _recoverSignatures.RecoverData(block);
                    Block[] processed = _branchProcessor.Process(
                        _parentHeader,
                        [block],
                        processingOptions,
                        NullBlockTracer.Instance);
                    LastProcessedBlock = processed[0];
                }

                Hash256 blockHash = block.GetOrCalculateHash();
                BlockRemoved?.Invoke(this, new BlockRemovedEventArgs(blockHash, ProcessingResult.Success));
            }
            catch (Exception ex)
            {
                Hash256 blockHash = block.GetOrCalculateHash();
                BlockRemoved?.Invoke(this, new BlockRemovedEventArgs(blockHash, ProcessingResult.Exception, ex));
                throw;
            }
            finally
            {
                if (CollectTiming && queueStart != 0)
                {
                    long queueTotalTicks = Stopwatch.GetTimestamp() - queueStart;
                    _owner.AddQueueTiming(block, senderRecoveryTicks, blockProcessTicks, queueTotalTicks);
                }

                if (Interlocked.Decrement(ref _count) == 0)
                {
                    ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record TimingBreakdownSummary
    {
        public TimingBreakdownSummary(
            string name,
            int iterations,
            long jsonParseTicks,
            long payloadDeserializeTicks,
            long optionalParamsTicks,
            long validateForkTicks,
            long validateParamsTicks,
            long handlerTicks,
            long queueTotalTicks,
            long senderRecoveryTicks,
            long blockProcessTicks,
            long txExecutionTicks,
            int txExecutionCount,
            long[] senderRecoveryByTypeTicks,
            int[] senderRecoveryByTypeCount,
            long[] txExecutionByTypeTicks,
            int[] txExecutionByTypeCount)
        {
            Name = name;
            Iterations = iterations;
            JsonParseTicks = jsonParseTicks;
            PayloadDeserializeTicks = payloadDeserializeTicks;
            OptionalParamsTicks = optionalParamsTicks;
            ValidateForkTicks = validateForkTicks;
            ValidateParamsTicks = validateParamsTicks;
            HandlerTicks = handlerTicks;
            QueueTotalTicks = queueTotalTicks;
            SenderRecoveryTicks = senderRecoveryTicks;
            BlockProcessTicks = blockProcessTicks;
            TxExecutionTicks = txExecutionTicks;
            TxExecutionCount = txExecutionCount;
            SenderRecoveryByTypeTicks = senderRecoveryByTypeTicks;
            SenderRecoveryByTypeCount = senderRecoveryByTypeCount;
            TxExecutionByTypeTicks = txExecutionByTypeTicks;
            TxExecutionByTypeCount = txExecutionByTypeCount;
        }

        public string Name { get; init; }
        public int Iterations { get; init; }
        public long JsonParseTicks { get; init; }
        public long PayloadDeserializeTicks { get; init; }
        public long OptionalParamsTicks { get; init; }
        public long ValidateForkTicks { get; init; }
        public long ValidateParamsTicks { get; init; }
        public long HandlerTicks { get; init; }
        public long QueueTotalTicks { get; init; }
        public long SenderRecoveryTicks { get; init; }
        public long BlockProcessTicks { get; init; }
        public long TxExecutionTicks { get; init; }
        public int TxExecutionCount { get; init; }
        public long[] SenderRecoveryByTypeTicks { get; init; }
        public int[] SenderRecoveryByTypeCount { get; init; }
        public long[] TxExecutionByTypeTicks { get; init; }
        public int[] TxExecutionByTypeCount { get; init; }
    }
}
