// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// The CPU affinity every harness in this campaign asserts in prose, read back from the operating system so
/// that a row states the environment it ran in rather than the one the runbook asked for.
/// </summary>
/// <remarks>
/// A frame-tx measurement taken on a contended core is not comparable with one taken on an idle core: the
/// ladder measurement has been observed to swing by 80% between repeats when other work shared core 0. Rows
/// therefore carry <c>cpus=</c> and <c>single_core=</c>, and harnesses whose quantity only means anything
/// under contention refuse to run without a single-core affinity.
/// </remarks>
internal static class MeasurementEnvironment
{
    /// <summary>The OS-observed CPU set, because in-process affinity is unreliable on Linux.</summary>
    public static string ObservedCpuSet()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (string line in File.ReadLines("/proc/self/status"))
                {
                    if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
                    {
                        return line["Cpus_allowed_list:".Length..].Trim();
                    }
                }
            }

            if (!OperatingSystem.IsWindows()) return "unknown";

            using Process current = Process.GetCurrentProcess();
            return $"mask:{(ulong)(nint)current.ProcessorAffinity:x}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException or Win32Exception or InvalidOperationException)
        {
            TestContext.Out.WriteLine($"DEBUG CPU affinity could not be read: {e.GetType().Name}: {e.Message}");
            return "unknown";
        }
    }

    public static bool IsSingleCore()
    {
        string set = ObservedCpuSet();

        if (set.StartsWith("mask:", StringComparison.Ordinal))
        {
            return ulong.TryParse(set["mask:".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                       out ulong mask)
                   && BitOperations.PopCount(mask) == 1;
        }

        return set.Length > 0
               && set != "unknown"
               && !set.Contains(',', StringComparison.Ordinal)
               && !set.Contains('-', StringComparison.Ordinal);
    }

    /// <summary>The affinity fields every measurement row carries.</summary>
    public static string CpuFields => $"cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")}";

    public static void SkipUnlessSingleCore()
    {
        if (IsSingleCore() || Environment.GetEnvironmentVariable("FRAME_FLOOD_ALLOW_MULTICORE") == "1") return;

        Assert.Ignore($"this process may run on CPUs [{ObservedCpuSet()}], so the single-core contention this "
                      + "harness measures does not hold and a flood would appear nearly free. Re-run under "
                      + "`taskset -c 0`, or set FRAME_FLOOD_ALLOW_MULTICORE=1 to measure the uncontended case "
                      + "deliberately.");
    }
}

/// <summary>Summary statistics shared by the frame-transaction measurement harnesses.</summary>
internal static class MeasurementStatistics
{
    /// <summary>Nearest-rank percentile, which keeps every reported figure tied to an observed sample.</summary>
    /// <remarks>Sorts a copy, so callers need not pass a sorted list and a call site moved between harnesses
    /// cannot silently read the wrong rank.</remarks>
    public static double Percentile(List<double> values, double quantile)
    {
        if (values.Count == 0) return double.NaN;

        List<double> sorted = [.. values];
        sorted.Sort();
        int rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}

/// <summary>Skips runs whose requested ceiling would be clamped by the compiled EIP-8141 limit.</summary>
internal static class Eip8141MeasurementGuards
{
    public static void SkipIfCeilingUnreachable(ulong ceiling)
    {
        if (ceiling > Eip8141Constants.MaxVerifyGas)
        {
            Assert.Ignore($"a {ceiling} ceiling is clamped to Eip8141Constants.MaxVerifyGas = "
                          + $"{Eip8141Constants.MaxVerifyGas}; the constant is compile-time inlined, so this point "
                          + "needs a source edit and a full rebuild.");
        }
    }
}

/// <summary>Counts production attempts and optionally measures their frame-gas burn.</summary>
/// <remarks>Burn tracing is disabled for timed paths because it selects the instrumented EVM path.</remarks>
internal sealed class CountingAdapter(ITransactionProcessorAdapter inner, bool measureBurn = true)
    : ITransactionProcessorAdapter
{
    public int Attempts { get; private set; }

    public List<ulong> BurnedPerAttempt { get; } = [];

    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        Attempts++;
        if (!measureBurn) return inner.Execute(transaction, txTracer);

        BudgetProbe probe = new();
        TransactionResult result = inner.Execute(transaction, new CompositeTxTracer(txTracer, probe));
        BurnedPerAttempt.Add(probe.Consumed);
        return result;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        inner.SetBlockExecutionContext(in blockExecutionContext);
}

/// <summary>Measures gas consumed from the observed instruction span rather than the declared limit.</summary>
internal sealed class BudgetProbe : TxTracer
{
    public BudgetProbe() => IsTracingInstructions = true;

    private ulong _high;
    private ulong _low = ulong.MaxValue;

    public long Operations { get; private set; }

    public ulong Consumed => _high >= _low && Operations > 0 ? _high - _low : 0;

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (gas > _high) _high = gas;
        if (gas < _low) _low = gas;
        Operations++;
    }
}

/// <summary>Runs never-approving frame transactions through the production transaction executor.</summary>
/// <remarks>
/// The rig holds one pre-built block per supplied transaction and cycles them, so a caller that needs each
/// build attempt to touch state no earlier attempt touched can supply a rotation without paying block
/// construction inside a timed window.
/// </remarks>
internal sealed class ProducerRig : IDisposable
{
    private readonly IReadOnlyTxProcessingScope _processingScope;
    private readonly IReadOnlyTxProcessorSource _processorSource;
    private readonly IReleaseSpec _spec;
    private BlockProcessor.BlockProductionTransactionsExecutor _executor = null!;
    private readonly int _kRetry;
    private readonly BlockReceiptsTracer _receiptsTracer = new();
    private readonly Block[] _blocks;
    private int _blockCursor;
    private int _attemptsOnCurrent;
    private CountingAdapter _adapter = null!;

    public int Evictions { get; private set; }

    private int _evictionsAtWindowStart;
    private int _executionsAtWindowStart;

    public int EvictionsInWindow => Evictions - _evictionsAtWindowStart;

    public int ExecutionsInWindow => FailingExecutions - _executionsAtWindowStart;

    public void MarkWindowStart()
    {
        _evictionsAtWindowStart = Evictions;
        _executionsAtWindowStart = FailingExecutions;
    }

    public int FailingExecutions => _adapter.Attempts;

    private ProducerRig(
        IReadOnlyTxProcessingScope processingScope, IReadOnlyTxProcessorSource processorSource,
        IReleaseSpec spec, IReadOnlyList<Transaction> txs, int kRetry, long blockGasLimit)
    {
        _processingScope = processingScope;
        _processorSource = processorSource;
        _spec = spec;
        _kRetry = kRetry;
        _receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);

        _blocks = new Block[txs.Count];
        for (int i = 0; i < txs.Count; i++)
        {
            _blocks[i] = Build.A.Block
                .WithNumber(1)
                .WithBaseFeePerGas(UInt256.Zero)
                .WithBeneficiary(TestItem.AddressE)
                .WithGasLimit((ulong)blockGasLimit)
                .WithTransactions(txs[i])
                .TestObject;
        }
    }

    /// <summary>
    /// Takes the processing stack from the chain's production wiring; only the executor under measurement,
    /// its counting adapter, the eviction gate the rig drives and a disabled block access list manager are
    /// built here.
    /// </summary>
    /// <remarks>The returned rig owns the processing scope and its source; nothing else does, so a throw
    /// before the rig is returned has to close them.</remarks>
    public static ProducerRig Create(
        BasicTestBlockchain chain, int kRetry, IReadOnlyList<Transaction> txs, long blockGasLimit)
    {
        ISpecProvider specProvider = chain.SpecProvider;
        IReleaseSpec spec = specProvider.GenesisSpec;

        IReadOnlyTxProcessorSource source = chain.ReadOnlyTxProcessingEnvFactory.Create();
        IReadOnlyTxProcessingScope? scope = null;
        try
        {
            scope = source.Build(chain.BlockTree.Head?.Header);
            IWorldState state = scope.WorldState;

            CountingAdapter adapter = new(
                new BuildUpTransactionProcessorAdapter(scope.TransactionProcessor), measureBurn: false);

            ProducerRig rig = new(scope, source, spec, txs, kRetry, blockGasLimit);

            IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
            balManager.Enabled.Returns(false);

            ITxPool gate = Substitute.For<ITxPool>();
            gate.EvictTransaction(Arg.Any<Transaction>()).Returns(_ => rig.OnEvictionRequested());

            rig._adapter = adapter;
            rig._executor = new BlockProcessor.BlockProductionTransactionsExecutor(
                adapter,
                state,
                new BlockProcessor.BlockProductionTransactionPicker(specProvider),
                LimboLogs.Instance,
                balManager,
                gate);

            return rig;
        }
        catch
        {
            scope?.Dispose();
            source.Dispose();
            throw;
        }
    }

    private bool OnEvictionRequested() => ++_attemptsOnCurrent >= _kRetry;

    public void RunFor(TimeSpan window)
    {
        long end = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end) ProduceOnce();
    }

    public List<double> Measure(TimeSpan window)
    {
        List<double> micros = [];
        long end = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            micros.Add(ProduceOnce());
        }
        return micros;
    }

    /// <summary>Runs one production pass and returns the microseconds it took.</summary>
    // Resetting the series avoids charging replacement construction differently across K_retry values.
    public double ProduceOnce()
    {
        Block block = _blocks[_blockCursor];
        _blockCursor = _blockCursor + 1 == _blocks.Length ? 0 : _blockCursor + 1;

        long start = Stopwatch.GetTimestamp();
        _receiptsTracer.StartNewBlockTrace(block);
        _executor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _spec));
        _executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, _receiptsTracer, CancellationToken.None);
        _receiptsTracer.EndBlockTrace();
        double micros = Stopwatch.GetElapsedTime(start).TotalMicroseconds;

        if (_attemptsOnCurrent >= _kRetry)
        {
            Evictions++;
            _attemptsOnCurrent = 0;
        }

        return micros;
    }

    public void Dispose()
    {
        _processingScope.Dispose();
        _processorSource.Dispose();
    }
}

/// <summary>The synthetic EIP-8141 validation-prefix workloads the measurement harnesses share.</summary>
internal static class FrameTxPrefixShapes
{
    /// <summary>Frame execution budget for a shape whose cost lives in the signature list, not the prefix.</summary>
    public const ulong MinimalFrameGas = 400;

    /// <summary>The campaign's ceiling matrix, shared by every harness in this assembly that sweeps it.</summary>
    /// <remarks>235,800 is the soispoke v2 declared validation budget: its pool VERIFY frame declares 225,000,
    /// and the recent-root frame and signature account for the remaining 10,800. Values above
    /// <see cref="Eip8141Constants.MaxVerifyGas"/> (300,000) self-ignore for EVM shapes unless the workflow
    /// raises the constant; signature-stuffed transactions are refused before that cap, so they exercise
    /// every ceiling on a stock build. <see cref="StuffedSignatureCount"/> floors and reserves frame gas, so a
    /// stuffed row may use up to one fewer signature than the ceiling permits.</remarks>
    public static readonly ulong[] SweptCeilings =
        [100_000ul, 235_800ul, 250_000ul, 300_000ul, 400_000ul, 500_000ul];

    public static int StuffedSignatureCount(ulong ceiling) =>
        (int)((ceiling - MinimalFrameGas) / Eip8141Constants.Secp256k1VerificationGasCost);

    public static byte[] Code(string shape) => shape switch
    {
        "keccak-wide" => Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(4096)
            .PushData(0)
            .Op(Instruction.KECCAK256)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done,
        "banned-opcode" => Prepare.EvmCode
            .Op(Instruction.TIMESTAMP)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown prefix shape")
    };
}
