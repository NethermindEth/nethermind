// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.IlEvm;

/// <summary>Counts every contract's frames and interpreted opcodes, for <see cref="IlEvmSettings.Profile"/>, and logs the busiest.</summary>
/// <remarks>
/// The log lines are the input for the shipped list: <c>IL-EVM profile &lt;rank&gt; &lt;keccak&gt; ops=… frames=… size=… cum=…</c>,
/// busiest first by interpreted opcodes. Nothing is compiled while profiling, so the counts are the interpreter's work.
/// </remarks>
public static class IlEvmProfile
{
    private sealed class Entry(int size)
    {
        public long Frames;
        public long Ops;
        public readonly int Size = size;
    }

    /// <summary>The counts by code hash; each code info's slot points at its entry, so no code info is kept alive.</summary>
    private static readonly ConcurrentDictionary<ValueHash256, Entry> _entries = new();
    private static int _dumped;

    private static Entry For(CodeInfo codeInfo)
    {
        ref object? slot = ref IlEvmFrameRunner.CodeSlot(codeInfo);
        if (slot is Entry entry)
            return entry;
        ValueHash256 hash = codeInfo.CodeHash == default ? ValueKeccak.Compute(codeInfo.CodeSpan) : codeInfo.CodeHash;
        entry = _entries.GetOrAdd(hash, static (_, size) => new Entry(size), codeInfo.CodeSpan.Length);
        // Profiling compiles nothing, so the slot is free; a lost race only means a second lookup.
        Interlocked.CompareExchange(ref slot, entry, null);
        return entry;
    }

    internal static void Frame(CodeInfo codeInfo, bool entry)
    {
        Entry counts = For(codeInfo);
        if (entry)
            Interlocked.Increment(ref counts.Frames);
    }

    internal static void Ops(CodeInfo codeInfo, int opCodes)
    {
        if (IlEvmFrameRunner.CodeSlot(codeInfo) is Entry counts)
            Interlocked.Add(ref counts.Ops, opCodes);
    }

    /// <summary>The profile so far by code, busiest first: keccak, interpreted opcodes, frames entered, code size.</summary>
    public static List<(ValueHash256 Hash, long Ops, long Frames, int Size)> Snapshot() =>
        [.. _entries.Where(static p => p.Value.Size > 0)
            .Select(static p => (p.Key, Interlocked.Read(ref p.Value.Ops), Interlocked.Read(ref p.Value.Frames), p.Value.Size))
            .OrderByDescending(static e => e.Item2)];

    /// <summary>Logs the busiest <paramref name="top"/> contracts once, at shutdown.</summary>
    public static void Dump(ILogger logger, int top)
    {
        if (Interlocked.Exchange(ref _dumped, 1) == 1)
            return;
        List<(ValueHash256 Hash, long Ops, long Frames, int Size)> entries = Snapshot();
        long totalOps = entries.Sum(static e => e.Ops);
        long totalFrames = entries.Sum(static e => e.Frames);
        // Console as well as the log: a shutting-down logger may already have stopped writing.
        void Write(string line)
        {
            if (logger.IsInfo)
                logger.Info(line);
            else
                Console.WriteLine(line);
        }

        Write($"IL-EVM profile: {entries.Count} contracts, {totalFrames} frames, {totalOps} interpreted opcodes");
        long cumulative = 0;
        for (int i = 0; i < Math.Min(top, entries.Count); i++)
        {
            (ValueHash256 hash, long ops, long frames, int size) = entries[i];
            cumulative += ops;
            Write(string.Create(CultureInfo.InvariantCulture,
                $"IL-EVM profile {i + 1} {hash} ops={ops} frames={frames} size={size} cum={(totalOps == 0 ? 0 : (double)cumulative / totalOps):F4}"));
        }

        Write("IL-EVM profile end");
    }

    /// <summary>Forgets everything counted, for tests; code infos profiled before keep pointing at their old entries.</summary>
    internal static void Reset()
    {
        _entries.Clear();
        _dumped = 0;
    }
}

/// <summary>The contracts shipped with the plugin to compile at startup: code hashes, busiest first.</summary>
public static class IlEvmPrecompileList
{
    public const string ResourceName = "Nethermind.IlEvm.Precompile.txt";

    /// <summary>The shipped list, or the one in <c>NETHERMIND_ILEVM_PRECOMPILE_LIST</c> when that names a file, for experiments.</summary>
    public static List<ValueHash256> Load()
    {
        string? path = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST");
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            return Parse(File.ReadLines(path));
        using Stream? stream = typeof(IlEvmPrecompileList).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return [];
        using StreamReader reader = new(stream);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return Parse(lines);
    }

    /// <summary>One code hash per line, the first token of 64 hex digits (with or without 0x); blank lines and lines starting with # are skipped; duplicates once.</summary>
    public static List<ValueHash256> Parse(IEnumerable<string> lines)
    {
        List<ValueHash256> hashes = [];
        HashSet<ValueHash256> seen = [];
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            foreach (string part in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
                if (token.Length != 64 || !token.All(Uri.IsHexDigit))
                    continue;
                ValueHash256 hash = new(Convert.FromHexString(token));
                if (seen.Add(hash))
                    hashes.Add(hash);
                break;
            }
        }

        return hashes;
    }
}

/// <summary>Compiles the shipped contracts before blocks are processed or calls served, and arms the profile's dump.</summary>
/// <remarks>
/// Runs after the blockchain is initialized, so the code database, the head and the specs are there, and before the
/// block processor starts and the RPC modules (the Engine API among them) are registered: a client is not ready for
/// payloads or calls until the compile is done, and none of it competes with block processing.
/// </remarks>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockchain)], dependents: [typeof(StartBlockProcessor), typeof(RegisterRpcModules)])]
public sealed class IlEvmStartupStep(
    IIlEvmConfig config,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IProcessExitSource processExit,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<IlEvmStartupStep>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (!IlEvmSettings.Enabled)
            return Task.CompletedTask;

        // What ran compiled, once the node stops: in a benchmark's log, next to its numbers.
        processExit.Token.Register(LogSummary);

        if (IlEvmSettings.Profile)
        {
            int top = Math.Max(1, config.ProfileTop);
            processExit.Token.Register(() => IlEvmProfile.Dump(_logger, top));
            AppDomain.CurrentDomain.ProcessExit += (_, _) => IlEvmProfile.Dump(_logger, top);
            if (_logger.IsInfo)
                _logger.Info($"IL-EVM profiling: counting frames and interpreted opcodes per contract, compiling nothing; the {top} busiest are logged at shutdown");
            return Task.CompletedTask;
        }

        return config.Precompile ? Task.Run(() => PrecompileSafely(cancellationToken), cancellationToken) : Task.CompletedTask;
    }

    /// <summary>The startup compile is an optional speed-up: whatever goes wrong leaves the node on the interpreter, never down.</summary>
    private void PrecompileSafely(CancellationToken cancellationToken)
    {
        try
        {
            Precompile(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM precompile failed; nothing precompiled, the interpreter runs everything: {exception}");
        }
    }

    private void LogSummary()
    {
        (int kept, int probation, int givenBack) = IlEvm.SelfCheckVerdicts;
        string line = string.Create(CultureInfo.InvariantCulture,
            $"IL-EVM at exit: {IlEvm.PrecompiledCount} precompiled, taken by {IlEvm.PrecompiledAttached} code states; " +
            $"{IlEvmSettings.CompiledContracts} compiled at run time, {IlEvmSettings.FailedContracts} declined, {IlEvmSettings.DeferredCompiles} deferred; " +
            $"self-check {kept} kept, {probation} probation, {givenBack} given back; compile thread busy " +
            $"{IlEvmSettings.CompileBusyTicks / (double)Stopwatch.Frequency:F1} s, idle scheduling {IlEvm.IlCompileQueue.IdleScheduling?.ToString() ?? "not started"}");
        if (_logger.IsInfo)
            _logger.Info(line);
        else
            Console.WriteLine(line);
    }

    /// <summary>The most IL and tables the startup compile may hold, past which the rest of the list is skipped.</summary>
    public static long MaxPrecompiledBytes { get; set; } =
        long.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_MAX_BYTES"), out long maxBytes) && maxBytes > 0 ? maxBytes : 768L << 20;

    private void Precompile(CancellationToken cancellationToken)
    {
        List<ValueHash256> list = IlEvmPrecompileList.Load();
        if (config.PrecompileLimit > 0 && list.Count > config.PrecompileLimit)
            list = list[..config.PrecompileLimit];
        if (list.Count == 0)
        {
            if (_logger.IsInfo)
                _logger.Info("IL-EVM precompile: no shipped contracts");
            return;
        }

        BlockHeader? head = blockTree.Head?.Header;
        if (head is null || head.Number == 0)
        {
            // A node at genesis is about to sync: the spec it would compile for is not the one its blocks will run.
            if (_logger.IsInfo)
                _logger.Info("IL-EVM precompile: skipped, the node is at genesis");
            return;
        }

        // The spec of the next block, which the first payload runs on: a slot after the head, so a fork activating at
        // the next block is the one compiled for.
        IReleaseSpec spec = specProvider.GetSpec(new ForkActivation(head.Number + 1, head.Timestamp + 12));
        if (IlEvm.BodySetFor(spec) is not { } bodies)
        {
            if (_logger.IsWarn)
                _logger.Warn("IL-EVM precompile: this core does not expose its opcode body sets; nothing precompiled");
            return;
        }

        IDb codeDb = dbProvider.CodeDb;
        ConcurrentDictionary<ValueHash256, IlEvm.IlContract> compiled = new();
        int missing = 0, failed = 0, skipped = 0;
        long ilBytes = 0, codeBytes = 0;
        Stopwatch wall = Stopwatch.StartNew();
        TimeSpan jitBefore = System.Runtime.JitInfo.GetCompilationTime();
        // One entry at a time, in list order, so under the memory cap the busiest are the ones compiled.
        Parallel.ForEach(Partitioner.Create(list, EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken }, hash =>
        {
            // Past the memory cap the rest of the list is skipped; the list is busiest first.
            if (Interlocked.Read(ref ilBytes) >= MaxPrecompiledBytes)
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            byte[]? code = codeDb.Get(hash.Bytes);
            if (code is null || code.Length == 0)
            {
                Interlocked.Increment(ref missing);
                return;
            }

            IlEvm.IlContract? contract = null;
            string? reason;
            try
            {
                contract = IlEvm.Precompile(code, bodies, out reason);
            }
            catch (Exception exception)
            {
                reason = exception.Message;
            }

            if (contract is null)
            {
                Interlocked.Increment(ref failed);
                IlEvmSettings.CountFailed(reason);
                return;
            }

            compiled[hash] = contract;
            Interlocked.Add(ref ilBytes, contract.MemoryBytes);
            Interlocked.Add(ref codeBytes, code.Length);
        });

        IlEvm.PublishPrecompiled(compiled);
        if (_logger.IsInfo)
            _logger.Info(string.Create(CultureInfo.InvariantCulture,
                $"IL-EVM precompile: {compiled.Count} of {list.Count} shipped contracts compiled for the spec of block {(head is null ? 0 : head.Number + 1)} " +
                $"({missing} not in the code database, {failed} declined, {skipped} past the {MaxPrecompiledBytes >> 20} MiB cap), {codeBytes / 1024} KiB of code, {ilBytes / (1 << 20)} MiB of IL and tables, " +
                $"{wall.Elapsed.TotalSeconds:F1} s on {Environment.ProcessorCount} threads, {(System.Runtime.JitInfo.GetCompilationTime() - jitBefore).TotalSeconds:F1} s of JIT"));
    }
}
