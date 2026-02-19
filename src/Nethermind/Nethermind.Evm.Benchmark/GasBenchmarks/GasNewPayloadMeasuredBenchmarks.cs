// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay gas-benchmark payload files through the full newPayload path:
/// JSON deserialization → ExecutionPayloadV3 → TryGetBlock → BranchProcessor.Process.
/// Each iteration re-deserializes JSON from memory, measuring the overhead of payload
/// decoding, RLP transaction decoding, and block construction on top of block processing.
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasNewPayloadMeasuredBenchmarks
{
    internal const string TimingFileEnvVar = "NETHERMIND_NEWPAYLOAD_TIMING_FILE";
    internal const string TimingReportFileEnvVar = "NETHERMIND_NEWPAYLOAD_TIMING_REPORT_FILE";
    private const int MaxConsoleScenarioCount = 10;

    private IWorldState _state;
    private BranchProcessor _branchProcessor;
    private BlockHeader _preBlockHeader;
    private byte[] _rawJsonBytes;
    private TimedTransactionProcessor _timedTransactionProcessor;
    private IBlockCachePreWarmer _preWarmer;
    private IDisposable _preWarmerLifetime;
    private RecoverSignatures _recoverSignatures;
    private ProcessingOptions _newPayloadProcessingOptions;
    private static readonly JsonSerializerOptions s_jsonOptions = EthereumJsonSerializer.JsonOptions;
    private static readonly EthereumEcdsa s_ecdsa = new(1);
    private static readonly object s_timingFileLock = new();

    // Timing breakdown accumulators (captured in ticks for sub-millisecond precision).
    private long _jsonParseTicks;
    private long _payloadDeserializeTicks;
    private long _optionalParamsTicks;
    private long _tryGetBlockTicks;
    private long _headerValidationTicks;
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
        IReleaseSpec pragueSpec = Prague.Instance;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);

        PayloadLoader.EnsureGenesisInitialized(GasPayloadBenchmarks.s_genesisPath, pragueSpec);

        TestBlockhashProvider blockhashProvider = new();
        BlockBenchmarkHelper.BranchProcessingContext branchProcessingContext = BlockBenchmarkHelper.CreateBranchProcessingContext(specProvider, blockhashProvider);
        _state = branchProcessingContext.State;
        _preWarmer = branchProcessingContext.PreWarmer;
        _preWarmerLifetime = branchProcessingContext.PreWarmerLifetime;
        _preBlockHeader = BlockBenchmarkHelper.CreateGenesisHeader();
        _preBlockHeader.TotalDifficulty = UInt256.Zero;
        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            _state,
            blockhashProvider,
            specProvider,
            branchProcessingContext.PreBlockCaches,
            branchProcessingContext.CachePrecompiles);
        _timedTransactionProcessor = new TimedTransactionProcessor(txProcessor, this);
        _recoverSignatures = new RecoverSignatures(s_ecdsa, specProvider, LimboLogs.Instance);

        BlockBenchmarkHelper.ExecuteSetupPayload(_state, _timedTransactionProcessor, _preBlockHeader, Scenario, specProvider);

        ReceiptConfig receiptConfig = new();
        IReceiptStorage receiptStorage = BlockBenchmarkHelper.CreateReceiptStorage(receiptConfig);
        _newPayloadProcessingOptions = BlockBenchmarkHelper.GetNewPayloadProcessingOptions(receiptConfig);

        BlockProcessor blockProcessor = BlockBenchmarkHelper.CreateBlockProcessor(
            specProvider, _timedTransactionProcessor, _state, receiptStorage);

        _branchProcessor = BlockBenchmarkHelper.CreateBranchProcessor(
            blockProcessor,
            specProvider,
            _state,
            _timedTransactionProcessor,
            blockhashProvider,
            _preWarmer);

        // Store raw JSON bytes for deserialization in each iteration
        string rawJson = PayloadLoader.ReadRawJson(Scenario.FilePath);
        _rawJsonBytes = System.Text.Encoding.UTF8.GetBytes(rawJson);

        // Warm up: deserialize + process once, then verify correctness
        Block block = DeserializeAndBuildBlock();
        Block[] result = _branchProcessor.Process(
            _preBlockHeader, [block],
            _newPayloadProcessingOptions,
            NullBlockTracer.Instance);
        PayloadLoader.VerifyProcessedBlock(result[0], Scenario.ToString(), Scenario.FilePath);
        ResetTimingAccumulators();
    }

    [Benchmark]
    public void ProcessBlock()
    {
        Block block = DeserializeAndBuildBlock(collectTiming: true);

        long processStart = Stopwatch.GetTimestamp();
        _branchProcessor.Process(
            _preBlockHeader, [block],
            _newPayloadProcessingOptions,
            NullBlockTracer.Instance);
        _blockProcessTicks += Stopwatch.GetTimestamp() - processStart;
        _iterationCount++;
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
        _tryGetBlockTicks = 0;
        _headerValidationTicks = 0;
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

    /// <summary>
    /// Deserializes the raw JSON-RPC payload into an ExecutionPayloadV3, extracts additional
    /// parameters (parentBeaconBlockRoot, executionRequests), and converts to a Block via TryGetBlock.
    /// </summary>
    private Block DeserializeAndBuildBlock(bool collectTiming = false)
    {
        long start = Stopwatch.GetTimestamp();
        using JsonDocument doc = JsonDocument.Parse(_rawJsonBytes);
        JsonElement paramsArray = doc.RootElement.GetProperty("params");
        if (collectTiming)
        {
            _jsonParseTicks += Stopwatch.GetTimestamp() - start;
        }

        // Deserialize ExecutionPayloadV3 from the first parameter
        start = Stopwatch.GetTimestamp();
        ExecutionPayloadV3 payload = JsonSerializer.Deserialize<ExecutionPayloadV3>(
            paramsArray[0].GetRawText(), s_jsonOptions);
        if (collectTiming)
        {
            _payloadDeserializeTicks += Stopwatch.GetTimestamp() - start;
        }

        start = Stopwatch.GetTimestamp();
        // Extract parentBeaconBlockRoot (params[2]) — separate JSON-RPC parameter
        if (paramsArray.GetArrayLength() > 2 && paramsArray[2].ValueKind == JsonValueKind.String)
        {
            payload.ParentBeaconBlockRoot = JsonSerializer.Deserialize<Hash256>(
                paramsArray[2].GetRawText(), s_jsonOptions);
        }

        // Extract executionRequests (params[3]) — Prague V4 parameter
        if (paramsArray.GetArrayLength() > 3 && paramsArray[3].ValueKind == JsonValueKind.Array)
        {
            payload.ExecutionRequests = JsonSerializer.Deserialize<byte[][]>(
                paramsArray[3].GetRawText(), s_jsonOptions);
        }
        if (collectTiming)
        {
            _optionalParamsTicks += Stopwatch.GetTimestamp() - start;
        }

        start = Stopwatch.GetTimestamp();
        BlockDecodingResult decodingResult = payload.TryGetBlock();
        if (decodingResult.Block is null)
        {
            throw new InvalidOperationException(
                $"Failed to decode block from payload: {decodingResult.Error}");
        }
        if (collectTiming)
        {
            _tryGetBlockTicks += Stopwatch.GetTimestamp() - start;
        }

        // Recover sender addresses — in production this happens during block validation
        Block block = decodingResult.Block;
        start = Stopwatch.GetTimestamp();
        block.Header.IsPostMerge = true;
        block.Header.TotalDifficulty = (_preBlockHeader.TotalDifficulty ?? UInt256.Zero) + block.Difficulty;
        if (!HeaderValidator.ValidateHash(block.Header, out Hash256 actualHash))
        {
            throw new InvalidOperationException(
                $"Payload block hash mismatch. Declared: {block.Header.Hash}, Calculated: {actualHash}");
        }
        if (collectTiming)
        {
            _headerValidationTicks += Stopwatch.GetTimestamp() - start;
        }

        if (collectTiming)
        {
            start = Stopwatch.GetTimestamp();
            _recoverSignatures.RecoverData(block);
            long elapsedTicks = Stopwatch.GetTimestamp() - start;
            _senderRecoveryTicks += elapsedTicks;
            AddSenderRecoveryTypeBreakdown(block, elapsedTicks);
        }
        else
        {
            _recoverSignatures.RecoverData(block);
        }

        return block;
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
                _tryGetBlockTicks,
                _headerValidationTicks,
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

        _preWarmer?.ClearCaches();
        _preWarmerLifetime?.Dispose();
        _state = null;
        _branchProcessor = null;
        _rawJsonBytes = null;
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
            Console.WriteLine($"NewPayloadMeasured timing breakdown saved to: {reportFilePath}");
        }
        else
        {
            Console.WriteLine($"NewPayloadMeasured timing breakdown saved to: {reportFilePath} (scenario count: {summaries.Count}, console output suppressed for runs with more than {MaxConsoleScenarioCount} scenarios)");
        }
    }

    private static void WriteReport(TextWriter writer, List<TimingBreakdownSummary> summaries)
    {
        writer.WriteLine("=== NewPayloadMeasured timing breakdown (final) ===");
        long totalJsonParseTicks = 0;
        long totalPayloadDeserializeTicks = 0;
        long totalOptionalParamsTicks = 0;
        long totalTryGetBlockTicks = 0;
        long totalHeaderValidationTicks = 0;
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
            PrintSummary(writer, summary.Name, summary.Iterations, summary.JsonParseTicks, summary.PayloadDeserializeTicks, summary.OptionalParamsTicks, summary.TryGetBlockTicks, summary.HeaderValidationTicks, summary.SenderRecoveryTicks, summary.BlockProcessTicks, summary.TxExecutionTicks, summary.TxExecutionCount, summary.SenderRecoveryByTypeTicks, summary.SenderRecoveryByTypeCount, summary.TxExecutionByTypeTicks, summary.TxExecutionByTypeCount);

            totalJsonParseTicks += summary.JsonParseTicks;
            totalPayloadDeserializeTicks += summary.PayloadDeserializeTicks;
            totalOptionalParamsTicks += summary.OptionalParamsTicks;
            totalTryGetBlockTicks += summary.TryGetBlockTicks;
            totalHeaderValidationTicks += summary.HeaderValidationTicks;
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
            PrintSummary(writer, "ALL SCENARIOS", totalIterations, totalJsonParseTicks, totalPayloadDeserializeTicks, totalOptionalParamsTicks, totalTryGetBlockTicks, totalHeaderValidationTicks, totalSenderRecoveryTicks, totalBlockProcessTicks, totalTxExecutionTicks, totalTxExecutionCount, totalSenderRecoveryByTypeTicks, totalSenderRecoveryByTypeCount, totalTxExecutionByTypeTicks, totalTxExecutionByTypeCount);
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
            $"nethermind-newpayload-timing-breakdown-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
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
        long tryGetBlockTicks,
        long headerValidationTicks,
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
        double tryGetBlockMs = TicksToMs(tryGetBlockTicks);
        double headerValidationMs = TicksToMs(headerValidationTicks);
        double senderRecoveryMs = TicksToMs(senderRecoveryTicks);
        double blockProcessMs = TicksToMs(blockProcessTicks);
        double txExecutionMs = TicksToMs(txExecutionTicks);
        double nonTxBlockMs = blockProcessMs - txExecutionMs;
        if (nonTxBlockMs < 0)
        {
            nonTxBlockMs = 0;
        }

        int senderRecoveryCount = GetTotalCount(senderRecoveryByTypeCount);
        double totalMs = jsonParseMs + payloadDeserializeMs + optionalParamsMs + tryGetBlockMs + headerValidationMs + senderRecoveryMs + blockProcessMs;

        writer.WriteLine($"--- {name} ({iterations} iterations) ---");
        PrintLine(writer, "JSON parse", jsonParseMs, totalMs, iterations);
        PrintLine(writer, "Payload deserialize", payloadDeserializeMs, totalMs, iterations);
        PrintLine(writer, "Optional params", optionalParamsMs, totalMs, iterations);
        PrintLine(writer, "TryGetBlock", tryGetBlockMs, totalMs, iterations);
        PrintLine(writer, "Header validate", headerValidationMs, totalMs, iterations);
        PrintLine(writer, "Sender recovery", senderRecoveryMs, totalMs, iterations);
        PrintLine(writer, "Block processing", blockProcessMs, totalMs, iterations);
        writer.WriteLine($"  {"Total",-20} {totalMs,9:F3} ms                 avg {totalMs / iterations:F3} ms/iter");
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
            _ => $"Type(0x{(byte)txType:X2})"
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

    private sealed class TimedTransactionProcessor(ITransactionProcessor inner, GasNewPayloadMeasuredBenchmarks owner) : ITransactionProcessor
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
            long tryGetBlockTicks,
            long headerValidationTicks,
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
            TryGetBlockTicks = tryGetBlockTicks;
            HeaderValidationTicks = headerValidationTicks;
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
        public long TryGetBlockTicks { get; init; }
        public long HeaderValidationTicks { get; init; }
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
