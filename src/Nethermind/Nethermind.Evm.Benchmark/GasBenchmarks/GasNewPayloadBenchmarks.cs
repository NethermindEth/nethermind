// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Visitors;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Trie;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay payload files through NewPayloadHandler flow to mirror
/// production newPayload processing (request decode + payload validation + handler + queue processing).
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasNewPayloadBenchmarks
{
    internal const string TimingFileEnvVar = "NETHERMIND_NEWPAYLOAD_REAL_TIMING_FILE";
    internal const string TimingReportFileEnvVar = "NETHERMIND_NEWPAYLOAD_REAL_TIMING_REPORT_FILE";
    private const int MaxConsoleScenarioCount = 10;
    private static readonly JsonSerializerOptions s_jsonOptions = EthereumJsonSerializer.JsonOptions;
    private static readonly EthereumEcdsa s_ecdsa = new(1);
    private static readonly object s_timingFileLock = new();

    private IReleaseSpec _releaseSpec;
    private ISpecProvider _specProvider;
    private IWorldState _state;
    private BranchProcessor _branchProcessor;
    private BlockHeader _preBlockHeader;
    private byte[] _rawJsonBytes;
    private BenchmarkNewPayloadBlockTree _blockTree;
    private BenchmarkProcessingQueue _processingQueue;
    private NewPayloadHandler _newPayloadHandler;
    private TimedTransactionProcessor _timedTransactionProcessor;
    private IBlockCachePreWarmer _preWarmer;
    private IDisposable _preWarmerLifetime;

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
        _releaseSpec = Prague.Instance;
        _specProvider = new SingleReleaseSpecProvider(_releaseSpec, 1, 1);

        PayloadLoader.EnsureGenesisInitialized(GasPayloadBenchmarks.s_genesisPath, _releaseSpec);

        TestBlockhashProvider blockhashProvider = new();
        BlockBenchmarkHelper.BranchProcessingContext branchProcessingContext = BlockBenchmarkHelper.CreateBranchProcessingContext(_specProvider, blockhashProvider);
        _state = branchProcessingContext.State;
        _preWarmer = branchProcessingContext.PreWarmer;
        _preWarmerLifetime = branchProcessingContext.PreWarmerLifetime;
        _preBlockHeader = BlockBenchmarkHelper.CreateGenesisHeader();
        _preBlockHeader.TotalDifficulty = UInt256.Zero;

        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            _state,
            blockhashProvider,
            _specProvider,
            branchProcessingContext.PreBlockCaches,
            branchProcessingContext.CachePrecompiles);
        _timedTransactionProcessor = new TimedTransactionProcessor(txProcessor, this);

        BlockBenchmarkHelper.ExecuteSetupPayload(_state, _timedTransactionProcessor, _preBlockHeader, Scenario, _releaseSpec);

        ReceiptConfig receiptConfig = new();
        IReceiptStorage receiptStorage = receiptConfig.StoreReceipts ? new InMemoryReceiptStorage() : NullReceiptStorage.Instance;
        BlockProcessor blockProcessor = BlockBenchmarkHelper.CreateBlockProcessor(
            _specProvider,
            _timedTransactionProcessor,
            _state,
            receiptStorage);

        _branchProcessor = new BranchProcessor(
            blockProcessor,
            _specProvider,
            _state,
            new BeaconBlockRootHandler(_timedTransactionProcessor, _state),
            blockhashProvider,
            LimboLogs.Instance,
            _preWarmer);

        string rawJson = PayloadLoader.ReadRawJson(Scenario.FilePath);
        _rawJsonBytes = Encoding.UTF8.GetBytes(rawJson);
        ExecutionPayloadParams<ExecutionPayloadV3> initialRequest = DeserializeRequest(collectTiming: false);

        _preBlockHeader.Hash = initialRequest.ExecutionPayload.ParentHash;
        long parentNumber = (long)initialRequest.ExecutionPayload.BlockNumber - 1;
        _preBlockHeader.Number = parentNumber >= 0 ? parentNumber : 0;

        _blockTree = new BenchmarkNewPayloadBlockTree(_preBlockHeader);
        _processingQueue = new BenchmarkProcessingQueue(
            _branchProcessor,
            _preBlockHeader,
            _specProvider,
            this);

        MergeConfig mergeConfig = new()
        {
            // Benchmark iterations replay the same block hash, so cache must be disabled.
            NewPayloadCacheSize = 0,
            SimulateBlockProduction = false,
        };

        _newPayloadHandler = new NewPayloadHandler(
            new NoopPayloadPreparationService(),
            Always.Valid,
            _blockTree,
            AlwaysPoS.Instance,
            Nethermind.Synchronization.No.BeaconSync,
            new StaticBeaconPivot(_preBlockHeader),
            new BlockCacheService(),
            _processingQueue,
            new NoopInvalidChainTracker(),
            new NoopMergeSyncController(),
            mergeConfig,
            receiptConfig,
            new WorldStateReaderAdapter(_state),
            LimboLogs.Instance);

        ExecuteNewPayload(initialRequest, collectTiming: false);

        Block processedBlock = _processingQueue.LastProcessedBlock;
        if (processedBlock is null)
        {
            throw new InvalidOperationException("Warmup did not produce a processed block.");
        }

        PayloadLoader.VerifyProcessedBlock(processedBlock, Scenario.ToString(), Scenario.FilePath);
        ResetTimingAccumulators();
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
        Nethermind.Merge.Plugin.Data.ValidationResult validationResult = request.ValidateParams(releaseSpec, EngineApiVersions.Prague, out string validationError);
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

    private void AddQueueTiming(Block block, long senderRecoveryTicks, long blockProcessTicks, long queueTotalTicks)
    {
        _senderRecoveryTicks += senderRecoveryTicks;
        _blockProcessTicks += blockProcessTicks;
        _queueTotalTicks += queueTotalTicks;
        AddSenderRecoveryTypeBreakdown(block, senderRecoveryTicks);
    }

    private void AddSenderRecoveryTypeBreakdown(Block block, long elapsedTicks)
    {
        int txCount = block.Transactions.Length;
        if (txCount == 0)
        {
            return;
        }

        int[] countsPerType = new int[_senderRecoveryByTypeCount.Length];
        int firstTypeIndex = -1;
        for (int i = 0; i < txCount; i++)
        {
            int txTypeIndex = (int)block.Transactions[i].Type;
            if (firstTypeIndex == -1)
            {
                firstTypeIndex = txTypeIndex;
            }

            countsPerType[txTypeIndex]++;
            _senderRecoveryByTypeCount[txTypeIndex]++;
        }

        long allocatedTicks = 0;
        for (int txTypeIndex = 0; txTypeIndex < countsPerType.Length; txTypeIndex++)
        {
            int count = countsPerType[txTypeIndex];
            if (count == 0)
            {
                continue;
            }

            long typeTicks = elapsedTicks * count / txCount;
            _senderRecoveryByTypeTicks[txTypeIndex] += typeTicks;
            allocatedTicks += typeTicks;
        }

        if (firstTypeIndex >= 0 && allocatedTicks != elapsedTicks)
        {
            _senderRecoveryByTypeTicks[firstTypeIndex] += elapsedTicks - allocatedTicks;
        }
    }

    private void AddTxExecutionTiming(TxType txType, long elapsedTicks)
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

        _newPayloadHandler?.Dispose();
        _preWarmer?.ClearCaches();
        _preWarmerLifetime?.Dispose();
        _newPayloadHandler = null;
        _processingQueue = null;
        _blockTree = null;
        _rawJsonBytes = null;
        _branchProcessor = null;
        _state = null;
        _timedTransactionProcessor = null;
        _preWarmer = null;
        _preWarmerLifetime = null;
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
            PrintSummary(
                writer,
                summary.Name,
                summary.Iterations,
                summary.JsonParseTicks,
                summary.PayloadDeserializeTicks,
                summary.OptionalParamsTicks,
                summary.ValidateForkTicks,
                summary.ValidateParamsTicks,
                summary.HandlerTicks,
                summary.QueueTotalTicks,
                summary.SenderRecoveryTicks,
                summary.BlockProcessTicks,
                summary.TxExecutionTicks,
                summary.TxExecutionCount,
                summary.SenderRecoveryByTypeTicks,
                summary.SenderRecoveryByTypeCount,
                summary.TxExecutionByTypeTicks,
                summary.TxExecutionByTypeCount);

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
            AddTypeBreakdown(totalSenderRecoveryByTypeTicks, summary.SenderRecoveryByTypeTicks);
            AddTypeBreakdown(totalSenderRecoveryByTypeCount, summary.SenderRecoveryByTypeCount);
            AddTypeBreakdown(totalTxExecutionByTypeTicks, summary.TxExecutionByTypeTicks);
            AddTypeBreakdown(totalTxExecutionByTypeCount, summary.TxExecutionByTypeCount);
        }

        if (summaries.Count > 1 && totalIterations > 0)
        {
            PrintSummary(
                writer,
                "ALL SCENARIOS",
                totalIterations,
                totalJsonParseTicks,
                totalPayloadDeserializeTicks,
                totalOptionalParamsTicks,
                totalValidateForkTicks,
                totalValidateParamsTicks,
                totalHandlerTicks,
                totalQueueTotalTicks,
                totalSenderRecoveryTicks,
                totalBlockProcessTicks,
                totalTxExecutionTicks,
                totalTxExecutionCount,
                totalSenderRecoveryByTypeTicks,
                totalSenderRecoveryByTypeCount,
                totalTxExecutionByTypeTicks,
                totalTxExecutionByTypeCount);
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

    private static void PrintSummary(
        TextWriter writer,
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
        double jsonParseMs = TicksToMs(jsonParseTicks);
        double payloadDeserializeMs = TicksToMs(payloadDeserializeTicks);
        double optionalParamsMs = TicksToMs(optionalParamsTicks);
        double validateForkMs = TicksToMs(validateForkTicks);
        double validateParamsMs = TicksToMs(validateParamsTicks);
        double handlerMs = TicksToMs(handlerTicks);
        double queueTotalMs = TicksToMs(queueTotalTicks);
        double senderRecoveryMs = TicksToMs(senderRecoveryTicks);
        double blockProcessMs = TicksToMs(blockProcessTicks);
        double txExecutionMs = TicksToMs(txExecutionTicks);
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

        int senderRecoveryCount = GetTotalCount(senderRecoveryByTypeCount);
        double totalMs = jsonParseMs + payloadDeserializeMs + optionalParamsMs + validateForkMs + validateParamsMs + handlerMs;

        writer.WriteLine($"--- {name} ({iterations} iterations) ---");
        PrintLine(writer, "JSON parse", jsonParseMs, totalMs, iterations);
        PrintLine(writer, "Payload deserialize", payloadDeserializeMs, totalMs, iterations);
        PrintLine(writer, "Optional params", optionalParamsMs, totalMs, iterations);
        PrintLine(writer, "Validate fork", validateForkMs, totalMs, iterations);
        PrintLine(writer, "Validate params", validateParamsMs, totalMs, iterations);
        PrintLine(writer, "Handler total", handlerMs, totalMs, iterations);
        writer.WriteLine($"  {"Total",-20} {totalMs,9:F3} ms                 avg {totalMs / iterations:F3} ms/iter");
        writer.WriteLine("  Handler detail:");
        writer.WriteLine($"    {"Queue total",-18} {queueTotalMs,9:F3} ms  ({GetShare(queueTotalMs, handlerMs),5:F1}% of handler)");
        writer.WriteLine($"    {"Handler non-queue",-18} {handlerNonQueueMs,9:F3} ms  ({GetShare(handlerNonQueueMs, handlerMs),5:F1}% of handler)");
        writer.WriteLine("  Queue detail:");
        writer.WriteLine($"    {"Sender recovery",-18} {senderRecoveryMs,9:F3} ms  ({GetShare(senderRecoveryMs, queueTotalMs),5:F1}% of queue)  avg {GetAverage(senderRecoveryMs, senderRecoveryCount):F3} ms/tx");
        writer.WriteLine($"    {"Block processing",-18} {blockProcessMs,9:F3} ms  ({GetShare(blockProcessMs, queueTotalMs),5:F1}% of queue)");
        writer.WriteLine($"    {"Queue non-staged",-18} {queueNonStagedMs,9:F3} ms  ({GetShare(queueNonStagedMs, queueTotalMs),5:F1}% of queue)");
        writer.WriteLine("  Block processing detail:");
        writer.WriteLine($"    {"Tx execution",-18} {txExecutionMs,9:F3} ms  ({GetShare(txExecutionMs, blockProcessMs),5:F1}% of block)  avg {GetAverage(txExecutionMs, txExecutionCount):F3} ms/tx");
        writer.WriteLine($"    {"Non-tx overhead",-18} {nonTxBlockMs,9:F3} ms  ({GetShare(nonTxBlockMs, blockProcessMs),5:F1}% of block)");
        PrintTxTypeBreakdown(writer, "Tx execution by type", txExecutionByTypeTicks, txExecutionByTypeCount, txExecutionMs);
        writer.WriteLine("  Sender recovery detail:");
        writer.WriteLine($"    {"Recovered txs",-18} {senderRecoveryCount,9} tx");
        writer.WriteLine($"    {"Avg per tx",-18} {GetAverage(senderRecoveryMs, senderRecoveryCount),9:F3} ms/tx");
        PrintTxTypeBreakdown(writer, "Sender recovery by type", senderRecoveryByTypeTicks, senderRecoveryByTypeCount, senderRecoveryMs);
    }

    private static void PrintLine(TextWriter writer, string label, double stageMs, double totalMs, int iterations)
    {
        double share = totalMs > 0 ? (100.0 * stageMs / totalMs) : 0.0;
        double avg = iterations > 0 ? (stageMs / iterations) : 0.0;
        writer.WriteLine($"  {label,-20} {stageMs,9:F3} ms  ({share,5:F1}%)  avg {avg:F3} ms/iter");
    }

    private static void PrintTxTypeBreakdown(TextWriter writer, string title, long[] ticksByType, int[] countByType, double stageTotalMs)
    {
        writer.WriteLine($"    {title}:");
        for (int txTypeIndex = 0; txTypeIndex < countByType.Length; txTypeIndex++)
        {
            int count = countByType[txTypeIndex];
            if (count == 0)
            {
                continue;
            }

            double ms = TicksToMs(ticksByType[txTypeIndex]);
            double share = GetShare(ms, stageTotalMs);
            double avg = GetAverage(ms, count);
            writer.WriteLine($"      {FormatTxType((TxType)txTypeIndex),-16} {ms,9:F3} ms  ({share,5:F1}%)  avg {avg:F3} ms/tx  count {count}");
        }
    }

    private static string FormatTxType(TxType txType)
    {
        return txType switch
        {
            TxType.Legacy => "Legacy",
            TxType.AccessList => "AccessList",
            TxType.EIP1559 => "EIP1559",
            TxType.Blob => "Blob",
            TxType.SetCode => "SetCode",
            TxType.DepositTx => "DepositTx",
            _ => $"Type(0x{(byte)txType:X2})",
        };
    }

    private static void AddTypeBreakdown(long[] destination, long[] source)
    {
        int length = destination.Length < source.Length ? destination.Length : source.Length;
        for (int i = 0; i < length; i++)
        {
            destination[i] += source[i];
        }
    }

    private static void AddTypeBreakdown(int[] destination, int[] source)
    {
        int length = destination.Length < source.Length ? destination.Length : source.Length;
        for (int i = 0; i < length; i++)
        {
            destination[i] += source[i];
        }
    }

    private static int GetTotalCount(int[] countByType)
    {
        int total = 0;
        for (int i = 0; i < countByType.Length; i++)
        {
            total += countByType[i];
        }

        return total;
    }

    private static double GetShare(double part, double whole)
    {
        return whole > 0 ? (100.0 * part / whole) : 0.0;
    }

    private static double GetAverage(double totalMs, int count)
    {
        return count > 0 ? (totalMs / count) : 0.0;
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private sealed class BenchmarkProcessingQueue : IBlockProcessingQueue
    {
        private readonly BranchProcessor _branchProcessor;
        private readonly BlockHeader _parentHeader;
        private readonly RecoverSignatures _recoverSignatures;
        private readonly GasNewPayloadBenchmarks _owner;
        private int _count;

        public BenchmarkProcessingQueue(
            BranchProcessor branchProcessor,
            BlockHeader parentHeader,
            ISpecProvider specProvider,
            GasNewPayloadBenchmarks owner)
        {
            _branchProcessor = branchProcessor;
            _parentHeader = parentHeader;
            _recoverSignatures = new RecoverSignatures(s_ecdsa, specProvider, LimboLogs.Instance);
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

    private sealed class TimedTransactionProcessor(ITransactionProcessor inner, GasNewPayloadBenchmarks owner) : ITransactionProcessor
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            long start = Stopwatch.GetTimestamp();
            TransactionResult result = inner.Execute(transaction, txTracer);
            long elapsedTicks = Stopwatch.GetTimestamp() - start;
            owner.AddTxExecutionTiming(transaction.Type, elapsedTicks);
            return result;
        }

        public TransactionResult CallAndRestore(Transaction transaction, ITxTracer txTracer) =>
            inner.CallAndRestore(transaction, txTracer);

        public TransactionResult BuildUp(Transaction transaction, ITxTracer txTracer) =>
            inner.BuildUp(transaction, txTracer);

        public TransactionResult Trace(Transaction transaction, ITxTracer txTracer) =>
            inner.Trace(transaction, txTracer);

        public TransactionResult Warmup(Transaction transaction, ITxTracer txTracer) =>
            inner.Warmup(transaction, txTracer);

        public void SetBlockExecutionContext(BlockHeader blockHeader) =>
            inner.SetBlockExecutionContext(blockHeader);

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
            inner.SetBlockExecutionContext(in blockExecutionContext);
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

    private sealed class BenchmarkNewPayloadBlockTree : IBlockTree
    {
        private readonly BlockHeader _parentHeader;
        private readonly Block _head;
        private readonly BlockInfo _parentInfo;

        public BenchmarkNewPayloadBlockTree(BlockHeader parentHeader)
        {
            _parentHeader = parentHeader;
            _head = new Block(parentHeader, new BlockBody(Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), null));
            _parentInfo = new BlockInfo(parentHeader.Hash, parentHeader.TotalDifficulty ?? UInt256.Zero)
            {
                WasProcessed = true,
                BlockNumber = parentHeader.Number,
            };
            BestSuggestedHeader = parentHeader;
            SyncPivot = (0, Keccak.Zero);
        }

        public Block Head => _head;
        public BlockHeader BestSuggestedHeader { get; set; }

        public event EventHandler<BlockReplacementEventArgs> BlockAddedToMain { add { } remove { } }
        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock { add { } remove { } }
        public event EventHandler<BlockEventArgs> NewSuggestedBlock { add { } remove { } }
        public event EventHandler<BlockEventArgs> NewHeadBlock { add { } remove { } }
        public event EventHandler<OnUpdateMainChainArgs> OnUpdateMainChain { add { } remove { } }
        public event EventHandler<IBlockTree.ForkChoiceUpdateEventArgs> OnForkChoiceUpdated { add { } remove { } }

        public BlockHeader FindBestSuggestedHeader() => BestSuggestedHeader;

        public Hash256 HeadHash => _head.Hash;
        public Hash256 GenesisHash => Keccak.Zero;
        public Hash256 PendingHash => null;
        public Hash256 FinalizedHash => null;
        public Hash256 SafeHash => null;
        public long? BestPersistedState { get; set; }

        public Block FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
        {
            if (blockHash == _parentHeader.Hash)
            {
                return _head;
            }

            return null;
        }

        public Block FindBlock(long blockNumber, BlockTreeLookupOptions options)
        {
            if (blockNumber == _parentHeader.Number)
            {
                return _head;
            }

            return null;
        }

        public bool HasBlock(long blockNumber, Hash256 blockHash) =>
            blockNumber == _parentHeader.Number && blockHash == _parentHeader.Hash;

        public BlockHeader FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
        {
            if (blockHash == _parentHeader.Hash)
            {
                return _parentHeader;
            }

            return null;
        }

        public BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options)
        {
            if (blockNumber == _parentHeader.Number)
            {
                return _parentHeader;
            }

            return null;
        }

        public Hash256 FindBlockHash(long blockNumber)
        {
            if (blockNumber == _parentHeader.Number)
            {
                return _parentHeader.Hash;
            }

            return null;
        }

        public bool IsMainChain(BlockHeader blockHeader) => blockHeader?.Hash == _parentHeader.Hash;

        public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => blockHash == _parentHeader.Hash;

        public long GetLowestBlock() => _parentHeader.Number;

        public ulong NetworkId => 1;
        public ulong ChainId => 1;
        public BlockHeader Genesis => _parentHeader;
        public Block BestSuggestedBody => _head;
        public BlockHeader BestSuggestedBeaconHeader => _parentHeader;
        public BlockHeader LowestInsertedHeader { get; set; }
        public BlockHeader LowestInsertedBeaconHeader { get; set; }
        public long BestKnownNumber => _parentHeader.Number;
        public long BestKnownBeaconNumber => _parentHeader.Number;
        public bool CanAcceptNewBlocks => true;
        public (long BlockNumber, Hash256 BlockHash) SyncPivot { get; set; }
        public bool IsProcessingBlock { get; set; }

        public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None)
            => AddBlockResult.Added;

        public void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) { }

        public AddBlockResult Insert(
            Block block,
            BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
            BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None,
            WriteFlags bodiesWriteFlags = WriteFlags.None)
            => AddBlockResult.Added;

        public void UpdateHeadBlock(Hash256 blockHash) { }

        public void NewOldestBlock(long oldestBlock) { }

        public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
            => AddBlockResult.Added;

        public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
            => ValueTask.FromResult(AddBlockResult.Added);

        public AddBlockResult SuggestHeader(BlockHeader header) => AddBlockResult.Added;

        public bool IsKnownBlock(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;

        public bool IsKnownBeaconBlock(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;

        public bool WasProcessed(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;

        public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false) { }

        public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) { }

        public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) => Task.CompletedTask;

        public (BlockInfo Info, ChainLevelInfo Level) GetInfo(long number, Hash256 blockHash)
        {
            if (number == _parentHeader.Number && blockHash == _parentHeader.Hash)
            {
                return (_parentInfo, null);
            }

            return (null, null);
        }

        public ChainLevelInfo FindLevel(long number) => null;

        public BlockInfo FindCanonicalBlockInfo(long blockNumber) => _parentInfo;

        public Hash256 FindHash(long blockNumber)
        {
            if (blockNumber == _parentHeader.Number)
            {
                return _parentHeader.Hash;
            }

            return null;
        }

        public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse)
            => new ArrayPoolList<BlockHeader>(0);

        public void DeleteInvalidBlock(Block invalidBlock) { }

        public void DeleteOldBlock(long blockNumber, Hash256 blockHash) { }

        public void ForkChoiceUpdated(Hash256 finalizedBlockHash, Hash256 safeBlockBlockHash) { }

        public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) => 0;

        public bool IsBetterThanHead(BlockHeader header) => false;

        public void UpdateBeaconMainChain(BlockInfo[] blockInfos, long clearBeaconMainChainStartPoint) { }

        public void RecalculateTreeLevels() { }
    }

#nullable enable
    private sealed class NoopPayloadPreparationService : IPayloadPreparationService
    {
        public string? StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes) => null;

        public ValueTask<IBlockProductionContext?> GetPayload(string payloadId, bool skipCancel = false)
            => ValueTask.FromResult<IBlockProductionContext?>(null);

        public void CancelBlockProduction(string payloadId) { }
    }
#nullable disable

    private sealed class NoopMergeSyncController : IMergeSyncController
    {
        public void StopSyncing() { }

        public bool TryInitBeaconHeaderSync(BlockHeader blockHeader) => false;

        public void StopBeaconModeControl() { }
    }

    private sealed class NoopInvalidChainTracker : IInvalidChainTracker
    {
        public void SetChildParent(Hash256 child, Hash256 parent) { }

        public void OnInvalidBlock(Hash256 failedBlock, Hash256 parent) { }

        public bool IsOnKnownInvalidChain(Hash256 blockHash, out Hash256 lastValidHash)
        {
            lastValidHash = null;
            return false;
        }

        public void Dispose() { }
    }

    private sealed class StaticBeaconPivot : IBeaconPivot
    {
        private readonly BlockHeader _pivot;

        public StaticBeaconPivot(BlockHeader pivot)
        {
            _pivot = pivot;
            ProcessDestination = pivot;
        }

        public long PivotNumber => _pivot.Number;
        public Hash256 PivotHash => _pivot.Hash;
        public Hash256 PivotParentHash => _pivot.ParentHash;
        public long PivotDestinationNumber => _pivot.Number;

        public BlockHeader ProcessDestination { get; set; }

        public bool ShouldForceStartNewSync { get; set; }

        public void EnsurePivot(BlockHeader blockHeader, bool updateOnlyIfNull = false)
        {
            if (!updateOnlyIfNull || ProcessDestination is null)
            {
                ProcessDestination = blockHeader;
            }
        }

        public void RemoveBeaconPivot() { }

        public bool BeaconPivotExists() => true;
    }

    private sealed class WorldStateReaderAdapter(IWorldState worldState) : IStateReader
    {
        public bool TryGetAccount(BlockHeader baseBlock, Address address, out AccountStruct account)
        {
            account = default;
            return false;
        }

        public ReadOnlySpan<byte> GetStorage(BlockHeader baseBlock, Address address, in UInt256 index) => [];

        public byte[] GetCode(Hash256 codeHash) => null;

        public byte[] GetCode(in ValueHash256 codeHash) => null;

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader baseBlock, VisitingOptions visitingOptions = null)
            where TCtx : struct, INodeContext<TCtx>
        {
        }

        public bool HasStateForBlock(BlockHeader baseBlock) => worldState.HasStateForBlock(baseBlock);
    }
}
