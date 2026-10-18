// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Consensus.Processing;
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
/// <remarks>The log lines are the input for the shipped list. Nothing is compiled while profiling, so the counts are the interpreter's work.</remarks>
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
/// <remarks>
/// An entry with <c>code=</c> compiles from the bytecode it carries: state-override code, which read-only calls send, is
/// in no code database. Its contract is found by content (<see cref="IlEvm.ShippedByContent"/>) where no cache stamped
/// the code with its keccak. The plugin ships no such entries; a list file <see cref="OverrideVariable"/> names may carry them.
/// </remarks>
public static class IlEvmPrecompileList
{
    public const string ResourceName = "Nethermind.IlEvm.Precompile.txt";

    /// <summary>The measurement candidates, read for <see cref="IlEvmPrecompileSource.Candidates"/>.</summary>
    public const string CandidatesResourceName = "Nethermind.IlEvm.PrecompileCandidates.txt";

    /// <summary>The variable naming a list file that replaces the embedded lists, for experiments.</summary>
    public const string OverrideVariable = "NETHERMIND_ILEVM_PRECOMPILE_LIST";

    /// <summary>A list entry: the code hash, and the fields its line gives beside it.</summary>
    /// <param name="Ops">The <c>ops=</c> count: the opcodes the code ran on the interpreter in the profile.</param>
    /// <param name="Frames">The <c>frames=</c> count: the frames the code ran in the profile.</param>
    /// <param name="Code">The <c>code=</c>: the bytecode, compiled once its keccak matches <paramref name="Hash"/>; null for code the code database holds.</param>
    public readonly record struct Entry(ValueHash256 Hash, long? Ops = null, long? Frames = null, int? Size = null, byte[]? Code = null)
    {
        /// <summary>The profile's opcodes per frame, or null when the line does not give both counts.</summary>
        public double? OpsPerFrame => Ops is { } ops && Frames is { } frames && frames > 0 ? (double)ops / frames : null;
    }

    /// <summary>Whether <see cref="OverrideVariable"/> names a list file, which then replaces the embedded lists.</summary>
    public static bool Overridden => OverridePath() is not null;

    private static string? OverridePath() => Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>The shipped list, or the one in <c>NETHERMIND_ILEVM_PRECOMPILE_LIST</c> when that names a file, for experiments.</summary>
    public static List<ValueHash256> Load() => Load(IlEvmPrecompileSource.Shipped);

    /// <summary>The hashes of <paramref name="source"/>'s list, or of the override file.</summary>
    public static List<ValueHash256> Load(IlEvmPrecompileSource source) => [.. LoadEntries(source, out _).Select(static e => e.Hash)];

    /// <summary>The entries of <paramref name="source"/>'s list, or of the override file; <paramref name="origin"/> names the source or the file's path.</summary>
    public static List<Entry> LoadEntries(IlEvmPrecompileSource source, out string origin)
    {
        if (OverridePath() is { } path)
        {
            origin = path;
            return ParseEntries(File.ReadLines(path));
        }

        origin = source.ToString();
        return ParseResource(source == IlEvmPrecompileSource.Candidates ? CandidatesResourceName : ResourceName);
    }

    private static List<Entry> ParseResource(string name)
    {
        using Stream? stream = typeof(IlEvmPrecompileList).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return [];
        using StreamReader reader = new(stream);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return ParseEntries(lines);
    }

    /// <summary>
    /// One code hash per line, the first token of 64 hex digits (with or without 0x), duplicates once. Blank lines, # lines and
    /// the stats dump's lines (<see cref="IlEvmStats.LinePrefix"/>) are skipped, so a log with both reads as the profile's lines alone.
    /// </summary>
    public static List<ValueHash256> Parse(IEnumerable<string> lines) => [.. ParseEntries(lines).Select(static e => e.Hash)];

    /// <summary>As <see cref="Parse"/>, with the fields that follow the hash on its line.</summary>
    public static List<Entry> ParseEntries(IEnumerable<string> lines)
    {
        List<Entry> entries = [];
        HashSet<ValueHash256> seen = [];
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.Contains(IlEvmStats.LinePrefix, StringComparison.Ordinal))
                continue;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[i][2..] : parts[i];
                if (token.Length != 64 || !token.All(Uri.IsHexDigit))
                    continue;
                ValueHash256 hash = new(Convert.FromHexString(token));
                if (seen.Add(hash))
                    entries.Add(WithFields(hash, parts.AsSpan(i + 1)));
                break;
            }
        }

        return entries;
    }

    private static Entry WithFields(ValueHash256 hash, ReadOnlySpan<string> tokens)
    {
        long? ops = null, frames = null;
        int? size = null;
        byte[]? code = null;
        foreach (string token in tokens)
        {
            if (Count(token, "ops=") is { } o)
                ops = o;
            else if (Count(token, "frames=") is { } f)
                frames = f;
            else if (Count(token, "size=") is { } s && s <= int.MaxValue)
                size = (int)s;
            else if (token.StartsWith("code=", StringComparison.Ordinal))
                code = Code(token.AsSpan("code=".Length));
        }

        return new Entry(hash, ops, frames, size, code);

        static long? Count(string token, string name) =>
            token.StartsWith(name, StringComparison.Ordinal) &&
            long.TryParse(token.AsSpan(name.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

        // Anything but whole hex bytes is no code: the entry then compiles from the code database.
        static byte[]? Code(ReadOnlySpan<char> hex)
        {
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];
            if (hex.Length == 0 || (hex.Length & 1) != 0 || hex.ContainsAnyExcept("0123456789abcdefABCDEF"))
                return null;
            return Convert.FromHexString(hex);
        }
    }

    /// <summary>The entries whose profile ran at least <paramref name="minOpsPerFrame"/> opcodes per frame, in order; those without the counts are kept.</summary>
    public static List<Entry> WithMinOpsPerFrame(List<Entry> entries, int minOpsPerFrame, out int skipped)
    {
        List<Entry> kept = minOpsPerFrame <= 0 ? entries : [.. entries.Where(e => e.OpsPerFrame is not { } perFrame || perFrame >= minOpsPerFrame)];
        skipped = entries.Count - kept.Count;
        return kept;
    }
}

/// <summary>Compiles the shipped contracts before blocks are processed or calls served, and arms the profile's and the stats' dumps.</summary>
/// <remarks>
/// Runs after the blockchain is initialized, so the code database, the head and the specs are there, and before the
/// block processor starts and the RPC modules (the Engine API among them) are registered: a client is not ready for
/// payloads or calls until the compile is done, and none of it competes with block processing.
/// </remarks>
/// <param name="mainProcessingContext">Resolved only for <see cref="IlEvmSettings.Stats"/>.</param>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockchain)], dependents: [typeof(StartBlockProcessor), typeof(RegisterRpcModules)])]
public sealed class IlEvmStartupStep(
    IIlEvmConfig config,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    Lazy<IMainProcessingContext> mainProcessingContext,
    IBlocksConfig blocksConfig,
    IProcessExitSource processExit,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<IlEvmStartupStep>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (!IlEvmSettings.Enabled)
            return Task.CompletedTask;

        processExit.Token.Register(LogSummary);
        // Code a cache stamped that the chain does not hold came with a request (IlEvm.IsRequestCode).
        IlEvm.ChainCode = dbProvider?.CodeDb;
        if (_logger.IsInfo)
            _logger.Info($"IL-EVM: mode {IlEvmSettings.Mode}, scope {IlEvmSettings.Scope}, read-only query surfaces {DescribeSurfaces()}; " +
                (IlTemperature.On ? $"temperature gate {IlTemperature.Threshold} on traces: {IlTemperature.DescribeRule()}; " : "") +
                "the exit line counts each surface's frame parts offered and run compiled");

        int top = Math.Max(1, config.ProfileTop);
        if (IlEvmSettings.Stats)
            ArmStats(top);

        if (IlEvmSettings.Profile)
        {
            // Under Stats the stats' dump logs the profile's lines.
            if (!IlEvmSettings.Stats)
            {
                processExit.Token.Register(() => IlEvmProfile.Dump(_logger, top));
                AppDomain.CurrentDomain.ProcessExit += (_, _) => IlEvmProfile.Dump(_logger, top);
            }

            if (_logger.IsInfo)
                _logger.Info($"IL-EVM profiling: counting frames and interpreted opcodes per contract, compiling nothing; the {top} busiest are logged at shutdown");
            return Task.CompletedTask;
        }

        return config.Precompile ? Task.Run(() => PrecompileSafely(cancellationToken), cancellationToken) : Task.CompletedTask;
    }

    private void ArmStats(int top)
    {
        int every = Math.Max(0, config.StatsSnapshotBlocks);
        ILogger logger = _logger;
        mainProcessingContext.Value.BranchProcessor.BlockProcessing += (_, _) =>
        {
            int blocks = IlEvmStats.BlockStarted();
            // Off block processing's thread: merging every thread's counts takes a while.
            if (every > 0 && blocks % every == 0)
                ThreadPool.UnsafeQueueUserWorkItem(static state => IlEvmStats.DumpSnapshot(state.logger, state.top, state.blocks), (logger, top, blocks), preferLocal: false);
        };
        processExit.Token.Register(() => IlEvmStats.Dump(_logger, top));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => IlEvmStats.Dump(_logger, top);
        if (_logger.IsInfo)
            _logger.Info($"IL-EVM timing: every contract's frame parts are timed on the path they take, interpreted or compiled, " +
                (config.Scope == IlEvmScope.All ? "in block processing and on the other threads" : $"in read-only queries only, of {DescribeSurfaces()} (the node's own block processing runs no runner with Scope=Rpc)") +
                $"; the self-check is off; the {top} contracts with the most time, and every contract that ran compiled, are logged at shutdown, with each surface's parts offered and compiled in the header");

        // Replayed transactions run nothing on block processing, and which are replayed depends on warming, which compiled code changes.
        if (config.Scope == IlEvmScope.All && blocksConfig.PreWarming != PreWarmMode.None && blocksConfig.PreWarmHandoff && _logger.IsWarn)
            _logger.Warn("IL-EVM timing: Blocks.PreWarmHandoff is on, so block processing replays the transactions whose pre-warm run finished first instead of running them; " +
                "for block-processing times that two runs can compare per contract, set --Blocks.PreWarmHandoff false");
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

    /// <summary>The kinds of query environment the runners were attached to, for the exit line: All, None, or the kinds joined by +.</summary>
    internal static string DescribeSurfaces() => IlEvmSettings.Surfaces.ToString().Replace(", ", "+", StringComparison.Ordinal);

    /// <summary>The exit line: what ran compiled, for a benchmark's log next to its numbers.</summary>
    private void LogSummary()
    {
        (int kept, int probation, int givenBack) = IlEvm.SelfCheckVerdicts;
        (int shortContracts, int longContracts) = IlEvmSettings.ShortPartVerdicts;
        string shortParts = IlEvmSettings.ShortParts > 0
            ? string.Create(CultureInfo.InvariantCulture, $"short-part rule {IlEvmSettings.ShortParts} opcodes: {shortContracts} to the interpreter, {longContracts} kept")
            : "short-part rule off";
        string line = string.Create(CultureInfo.InvariantCulture,
            $"IL-EVM at exit: mode {IlEvmSettings.Mode}, scope {IlEvmSettings.Scope}, surfaces {DescribeSurfaces()}; {IlSurfaceCounts.Describe()}; " +
            $"list {IlShippedBacklog.Compiled} compiled, {IlShippedBacklog.MissingCount} missing, {IlShippedBacklog.Declined} declined; " +
            $"{IlEvm.PrecompiledCount} precompiled, taken by {IlEvm.PrecompiledAttached} code states; " +
            $"{IlEvmSettings.CompiledContracts} compiled at run time, {IlEvmSettings.FailedContracts} declined, {IlEvmSettings.DeferredCompiles} deferred; " +
            $"self-check {kept} kept, {probation} probation, {givenBack} given back (keep ratio {IlEvmSettings.SelfCheckKeepRatio}); {shortParts}; {IlTemperature.Describe()}; " +
            $"decided entry {(IlEvmSettings.DecidedEntry ? "on" : "off")}; compile thread busy " +
            $"{IlEvmSettings.CompileBusyTicks / (double)Stopwatch.Frequency:F1} s on {IlEvmSettings.CompileThreads} thread(s), " +
            $"order {(IlEvmSettings.CompileOrderHot ? "hot" : "fifo")}, entry chunk {(IlEvmSettings.EntryChunk ? "on" : "off")}, " +
            $"check cold restart {(IlEvmSettings.SelfCheckColdRestart ? "on" : "off")}, " +
            $"idle scheduling {IlEvm.IlCompileQueue.IdleScheduling?.ToString() ?? "not started"}") + IlEvmSettings.DescribeCaught();
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
        List<IlEvmPrecompileList.Entry> entries = IlEvmPrecompileList.LoadEntries(config.PrecompileList, out string origin);
        int listed = entries.Count;
        entries = IlEvmPrecompileList.WithMinOpsPerFrame(entries, config.PrecompileMinOpsPerFrame, out int belowMin);
        if (config.PrecompileLimit > 0 && entries.Count > config.PrecompileLimit)
            entries = entries[..config.PrecompileLimit];
        string source = config.PrecompileMinOpsPerFrame > 0
            ? $"list {origin}, {belowMin} of {listed} entries under {config.PrecompileMinOpsPerFrame} opcodes per frame skipped"
            : $"list {origin}";
        if (entries.Count == 0)
        {
            if (_logger.IsInfo)
                _logger.Info($"IL-EVM precompile: no shipped contracts ({source})");
            return;
        }

        // The next block's spec, 12 seconds after the head, so a fork activating there is the one compiled for; nothing at
        // genesis, where the node has yet to sync.
        BlockHeader? head = blockTree.Head?.Header;
        if (head is null || head.Number == 0)
        {
            if (_logger.IsInfo)
                _logger.Info("IL-EVM precompile: skipped, the node is at genesis");
            return;
        }

        IReleaseSpec spec = specProvider.GetSpec(new ForkActivation(head.Number + 1, head.Timestamp + 12));
        string target = $"the spec of block {head.Number + 1}";
        if (IlEvm.BodySetFor(spec) is not { } bodies)
        {
            if (_logger.IsWarn)
                _logger.Warn("IL-EVM precompile: the core has no opcode body set for this spec; nothing precompiled");
            return;
        }

        IDb codeDb = dbProvider.CodeDb;
        IlEvm.IlContract?[] compiled = new IlEvm.IlContract?[entries.Count];
        ConcurrentBag<int> missing = [];
        int failed = 0, skipped = 0;
        long ilBytes = 0, codeBytes = 0;
        Stopwatch wall = Stopwatch.StartNew();
        TimeSpan jitBefore = System.Runtime.JitInfo.GetCompilationTime();
        // One entry at a time, in list order, so under the memory cap the busiest are the ones compiled.
        Parallel.ForEach(Partitioner.Create(Enumerable.Range(0, entries.Count), EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken }, index =>
        {
            if (Interlocked.Read(ref ilBytes) >= MaxPrecompiledBytes)
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            switch (IlShippedBacklog.TryCompile(codeDb, entries[index], bodies, out IlEvm.IlContract? contract, out int codeLength))
            {
                case IlShippedBacklog.Outcome.Missing:
                    missing.Add(index);
                    return;
                case IlShippedBacklog.Outcome.Declined:
                    Interlocked.Increment(ref failed);
                    return;
            }

            compiled[index] = contract;
            Interlocked.Add(ref ilBytes, contract!.MemoryBytes);
            Interlocked.Add(ref codeBytes, codeLength);
        });

        List<(IlEvmPrecompileList.Entry Entry, IlEvm.IlContract Contract)> shipped = [];
        for (int index = 0; index < entries.Count; index++)
        {
            if (compiled[index] is { } contract)
                shipped.Add((entries[index], contract));
        }

        IlEvm.PublishShipped(shipped);
        IlShippedBacklog.Record([.. missing.Order().Select(i => entries[i])], shipped.Count, failed);
        if (_logger.IsInfo)
        {
            _logger.Info(string.Create(CultureInfo.InvariantCulture,
                $"IL-EVM precompile: {shipped.Count} of {entries.Count} shipped contracts compiled for {target} " +
                $"({source}; {missing.Count} not in the code database, {failed} declined, {skipped} past the {MaxPrecompiledBytes >> 20} MiB cap" +
                $"), {codeBytes / 1024} KiB of code, {ilBytes / (1 << 20)} MiB of IL and tables, " +
                $"{wall.Elapsed.TotalSeconds:F1} s on {Environment.ProcessorCount} threads, {(System.Runtime.JitInfo.GetCompilationTime() - jitBefore).TotalSeconds:F1} s of JIT"));
        }
    }
}

/// <summary>The startup compile's tally of the list: the entries compiled, those declined, and those whose code the code database lacked; and the compile of one entry.</summary>
internal static class IlShippedBacklog
{
    internal enum Outcome
    {
        Compiled,
        Missing,
        Declined,
    }

    private static readonly object _lock = new();
    private static readonly List<IlEvmPrecompileList.Entry> _missing = [];
    private static int _compiled;
    private static int _declined;

    /// <summary>List entries compiled at startup.</summary>
    public static int Compiled => Volatile.Read(ref _compiled);

    public static int Declined => Volatile.Read(ref _declined);

    /// <summary>Entries whose code was not in the code database at startup.</summary>
    public static int MissingCount
    {
        get
        {
            lock (_lock)
                return _missing.Count;
        }
    }

    public static List<IlEvmPrecompileList.Entry> Missing()
    {
        lock (_lock)
            return [.. _missing];
    }

    internal static void Record(IEnumerable<IlEvmPrecompileList.Entry> missing, int compiled, int declined)
    {
        lock (_lock)
        {
            _missing.Clear();
            _missing.AddRange(missing);
            Volatile.Write(ref _compiled, compiled);
            Volatile.Write(ref _declined, declined);
        }
    }

    /// <summary>The code <paramref name="entry"/> carries, else the code database's for its hash; null when there is none.</summary>
    /// <param name="mismatched">Whether the carried code's keccak is not the entry's hash, which never compiles: a shipped contract runs wherever its keccak is met.</param>
    internal static byte[]? CodeOf(IDb codeDb, IlEvmPrecompileList.Entry entry, out bool mismatched)
    {
        mismatched = false;
        if (entry.Code is not { } carried)
            return codeDb.Get(entry.Hash.Bytes);
        if (ValueKeccak.Compute(carried) == entry.Hash)
            return carried;
        mismatched = true;
        return null;
    }

    internal const string MismatchedCode = "the list's code does not match its hash";

    /// <summary>Compiles the code of <paramref name="entry"/> for <paramref name="bodies"/>, every chunk at once; a failure counts as any compiler failure.</summary>
    internal static Outcome TryCompile(IDb codeDb, IlEvmPrecompileList.Entry entry, OpcodeBodySet bodies, out IlEvm.IlContract? contract, out int codeLength)
    {
        contract = null;
        byte[]? code = CodeOf(codeDb, entry, out bool mismatched);
        codeLength = code?.Length ?? 0;
        if (mismatched)
        {
            IlEvmSettings.CountFailed(MismatchedCode);
            return Outcome.Declined;
        }

        if (code is null || code.Length == 0)
            return Outcome.Missing;

        string? reason;
        try
        {
            contract = IlEvm.Precompile(code, bodies, out reason);
        }
        catch (Exception exception)
        {
            reason = exception.Message;
        }

        if (contract is not null)
            return Outcome.Compiled;
        IlEvmSettings.CountFailed(reason);
        return Outcome.Declined;
    }

    internal static void Reset()
    {
        lock (_lock)
        {
            _missing.Clear();
            Volatile.Write(ref _compiled, 0);
            Volatile.Write(ref _declined, 0);
        }
    }
}
