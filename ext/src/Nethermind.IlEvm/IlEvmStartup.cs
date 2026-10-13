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
    private sealed class Entry
    {
        public long Frames;
        public long Ops;
    }

    private static readonly ConcurrentDictionary<CodeInfo, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private static int _dumped;

    internal static void Frame(CodeInfo codeInfo) => Interlocked.Increment(ref _entries.GetOrAdd(codeInfo, static _ => new Entry()).Frames);

    internal static void Ops(CodeInfo codeInfo, int opCodes)
    {
        if (_entries.TryGetValue(codeInfo, out Entry? entry))
            Interlocked.Add(ref entry.Ops, opCodes);
    }

    /// <summary>The profile so far by code, busiest first: keccak, interpreted opcodes, frames, code size.</summary>
    public static List<(ValueHash256 Hash, long Ops, long Frames, int Size)> Snapshot()
    {
        Dictionary<ValueHash256, (long Ops, long Frames, int Size)> byCode = [];
        foreach ((CodeInfo codeInfo, Entry entry) in _entries)
        {
            if (codeInfo.CodeSpan.Length == 0)
                continue;
            ValueHash256 hash = codeInfo.CodeHash == default ? ValueKeccak.Compute(codeInfo.CodeSpan) : codeInfo.CodeHash;
            (long ops, long frames, int _) = byCode.GetValueOrDefault(hash);
            byCode[hash] = (ops + Interlocked.Read(ref entry.Ops), frames + Interlocked.Read(ref entry.Frames), codeInfo.CodeSpan.Length);
        }

        return [.. byCode.Select(static p => (p.Key, p.Value.Ops, p.Value.Frames, p.Value.Size)).OrderByDescending(static e => e.Ops)];
    }

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

    /// <summary>Forgets everything counted, for tests.</summary>
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

    /// <summary>One code hash per line, first token, with or without 0x; blank lines and lines starting with # are skipped; duplicates once.</summary>
    public static List<ValueHash256> Parse(IEnumerable<string> lines)
    {
        List<ValueHash256> hashes = [];
        HashSet<ValueHash256> seen = [];
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string token = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                token = token[2..];
            if (token.Length != 64)
                continue;
            ValueHash256 hash = new(Convert.FromHexString(token));
            if (seen.Add(hash))
                hashes.Add(hash);
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

        return config.Precompile ? Task.Run(Precompile, cancellationToken) : Task.CompletedTask;
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

    private void Precompile()
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
        // The spec of the next block, which the first payload runs on.
        IReleaseSpec spec = head is null ? specProvider.GenesisSpec : specProvider.GetSpec(new ForkActivation(head.Number + 1, head.Timestamp + 1));
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
        Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, hash =>
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
