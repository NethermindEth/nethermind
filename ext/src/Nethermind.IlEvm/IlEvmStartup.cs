// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
using Nethermind.TxPool;

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

/// <summary>The shape of a contract's visits, as a list's <c>L=</c> gives it: the opcodes a visit runs for each byte of the code.</summary>
public enum IlEvmCodeShape : byte
{
    /// <summary>Straight-line code: a visit runs fewer opcodes than a tenth of the code's size.</summary>
    Line,
    /// <summary>Between straight-line code and loops.</summary>
    Mixed,
    /// <summary>Loops: a visit runs at least as many opcodes as the code has bytes.</summary>
    Loop,
}

/// <summary>The kinds of block-processing thread a list found compiled code gains on for a contract, as its <c>use=</c> gives them.</summary>
[Flags]
public enum IlEvmUse : byte
{
    None = 0,
    /// <summary><c>m</c>: the block-processing thread.</summary>
    Main = 1,
    /// <summary><c>w</c>: the other threads block processing's runner serves, prewarming first.</summary>
    Workers = 2,
}

/// <summary>The contracts shipped with the plugin to compile at startup: code hashes, busiest first.</summary>
/// <remarks>
/// <para>
/// A list line names its contract by the first token of 64 hex digits and may go on with fields: the profile's
/// <c>ops= frames= size=</c>, the measured list's <c>score= use= minv= L=</c>, and <c>qminv= code=</c>. Lines starting
/// <c># calib</c> carry the constants the list's Stats runs fitted.
/// </para>
/// <para>
/// An entry with <c>code=</c> carries its bytecode, and compiles from it rather than from the code database: the code of
/// a state override, which read-only calls send with every request, is in no code database. Its contract is found by its
/// content (<see cref="IlRuntime.ShippedByContent"/>), as no cache stamps override code with its keccak, and by its
/// keccak where a cache does. A list file <c>NETHERMIND_ILEVM_PRECOMPILE_LIST</c> names may carry such entries; the
/// plugin ships none (<see cref="LoadRpcList"/>).
/// </para>
/// </remarks>
public static class IlEvmPrecompileList
{
    public const string ResourceName = "Nethermind.IlEvm.Precompile.txt";

    /// <summary>The measurement candidates, read for <see cref="IlEvmPrecompileSource.Candidates"/>.</summary>
    public const string CandidatesResourceName = "Nethermind.IlEvm.PrecompileCandidates.txt";

    /// <summary>The variable naming a list file that replaces the embedded lists, for experiments.</summary>
    public const string OverrideVariable = "NETHERMIND_ILEVM_PRECOMPILE_LIST";

    /// <summary>The prefix of a calibration line.</summary>
    public const string CalibrationPrefix = "# calib";

    /// <summary>A list entry: the code hash, and the fields its line gives beside it.</summary>
    /// <param name="Ops">The <c>ops=</c> count: the opcodes the code ran on the interpreter in the profile.</param>
    /// <param name="Frames">The <c>frames=</c> count: the frames the code ran in the profile.</param>
    /// <param name="Size">The <c>size=</c> count: the code's size in bytes.</param>
    /// <param name="Score">The <c>score=</c>: the nanoseconds per block compiling the contract saved in the Stats runs.</param>
    /// <param name="Use">The <c>use=</c> letters, <c>m</c> and <c>w</c>: the block-processing threads compiled code may run the contract on.</param>
    /// <param name="MinVisitLog2">The <c>minv=</c>: the log2 of the visit length from which block processing's runner admits the contract.</param>
    /// <param name="Shape">The <c>L=</c>: <c>line</c>, <c>mixed</c> or <c>loop</c>.</param>
    /// <param name="QueryMinVisitLog2">
    /// The <c>qminv=</c>: the log2 of the visit length from which the read-only queries' runner admits the contract from a
    /// visit's first part, its visits of a key not learned yet admitted too rather than rented; 0 admits every visit, from
    /// the first. Null leaves the contract to the runner's own thresholds, learned visit by visit.
    /// </param>
    /// <param name="Code">The <c>code=</c>: the contract's bytecode, which the startup compile compiles once its keccak matches <paramref name="Hash"/>; null for code the code database holds.</param>
    public readonly record struct Entry(ValueHash256 Hash, long? Ops = null, long? Frames = null, int? Size = null,
        double? Score = null, IlEvmUse? Use = null, int? MinVisitLog2 = null, IlEvmCodeShape? Shape = null,
        int? QueryMinVisitLog2 = null, byte[]? Code = null)
    {
        /// <summary>The profile's opcodes per frame, or null when the line does not give both counts.</summary>
        public double? OpsPerFrame => Ops is { } ops && Frames is { } frames && frames > 0 ? (double)ops / frames : null;
    }

    /// <summary>A list as read: its entries in list order, and its calibration lines without their prefix.</summary>
    public sealed record ListFile(List<Entry> Entries, List<string> Calibration);

    /// <summary>Whether <see cref="OverrideVariable"/> names a list file, which then replaces the embedded lists.</summary>
    public static bool Overridden => OverridePath() is not null;

    private static string? OverridePath() => Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>The shipped list, or the one in <c>NETHERMIND_ILEVM_PRECOMPILE_LIST</c> when that names a file, for experiments.</summary>
    public static List<ValueHash256> Load() => Load(IlEvmPrecompileSource.Shipped);

    /// <summary>The hashes of <paramref name="source"/>'s list, or of the file <c>NETHERMIND_ILEVM_PRECOMPILE_LIST</c> names, which overrides either.</summary>
    public static List<ValueHash256> Load(IlEvmPrecompileSource source) => [.. LoadEntries(source, out _).Select(static e => e.Hash)];

    /// <summary>The entries of <paramref name="source"/>'s list, or of the file <c>NETHERMIND_ILEVM_PRECOMPILE_LIST</c> names, which overrides either.</summary>
    /// <param name="origin">Where the entries came from, for the log: the source's name, or the file's path.</param>
    public static List<Entry> LoadEntries(IlEvmPrecompileSource source, out string origin) => LoadList(source, out origin).Entries;

    /// <summary>As <see cref="LoadEntries"/>, with the list's calibration lines.</summary>
    public static ListFile LoadList(IlEvmPrecompileSource source, out string origin)
    {
        if (OverridePath() is { } path)
        {
            origin = path;
            return ParseList(File.ReadLines(path));
        }

        origin = source.ToString();
        return ParseResource(source == IlEvmPrecompileSource.Candidates ? CandidatesResourceName : ResourceName);
    }

    /// <summary>
    /// The RPC list: contracts read-only calls send as state-override code, each entry with its bytecode, which the
    /// Adaptive rules compile at startup ahead of the block list. The plugin ships no RPC list, so this is empty; a list
    /// file <c>NETHERMIND_ILEVM_PRECOMPILE_LIST</c> names may carry entries with their code (<c>code=</c>) instead, which
    /// then compile as part of the block list. Tests set <see cref="RpcLinesForTests"/>.
    /// </summary>
    /// <param name="origin">Where the entries came from, for the log.</param>
    public static ListFile LoadRpcList(out string origin)
    {
        if (OverridePath() is { } path)
        {
            origin = path;
            return new ListFile([], []);
        }

        origin = "Rpc";
        return RpcLinesForTests is { } lines ? ParseList(lines) : new ListFile([], []);
    }

    /// <summary>The RPC list's lines in tests; null, as in a node, for none.</summary>
    internal static IReadOnlyList<string>? RpcLinesForTests { get; set; }

    /// <summary>An embedded list; empty when the plugin carries no such resource.</summary>
    private static ListFile ParseResource(string name)
    {
        using Stream? stream = typeof(IlEvmPrecompileList).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return new ListFile([], []);
        using StreamReader reader = new(stream);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return ParseList(lines);
    }

    /// <summary>
    /// One code hash per line, the first token of 64 hex digits (with or without 0x); blank lines, lines starting with #,
    /// and the stats dump's lines are skipped; duplicates once.
    /// </summary>
    /// <remarks>
    /// The stats dump's lines (<see cref="IlEvmStats.LinePrefix"/>) rank contracts by time and carry no <c>ops=</c> or
    /// <c>frames=</c>, so a log with both a stats dump and the profile's lines reads as the profile's lines alone.
    /// </remarks>
    public static List<ValueHash256> Parse(IEnumerable<string> lines) => [.. ParseEntries(lines).Select(static e => e.Hash)];

    /// <summary>As <see cref="Parse"/>, with the fields that follow the hash on its line, as the profile and the list tooling write them.</summary>
    public static List<Entry> ParseEntries(IEnumerable<string> lines) => ParseList(lines).Entries;

    /// <summary>As <see cref="ParseEntries"/>, with the calibration lines, <c># calib</c> and what follows, kept in order without the prefix.</summary>
    public static ListFile ParseList(IEnumerable<string> lines)
    {
        List<Entry> entries = [];
        List<string> calibration = [];
        HashSet<ValueHash256> seen = [];
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith(CalibrationPrefix, StringComparison.Ordinal) && (line.Length == CalibrationPrefix.Length || char.IsWhiteSpace(line[CalibrationPrefix.Length])))
            {
                calibration.Add(line[CalibrationPrefix.Length..].Trim());
                continue;
            }

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

        return new ListFile(entries, calibration);
    }

    /// <summary>The calibration lines' <c>name=value</c> tokens, the last of a name winning; other tokens are skipped.</summary>
    public static Dictionary<string, string> CalibrationValues(IEnumerable<string> calibration)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in calibration)
        {
            foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = token.IndexOf('=');
                if (equals > 0)
                    values[token[..equals]] = token[(equals + 1)..];
            }
        }

        return values;
    }

    private static Entry WithFields(ValueHash256 hash, ReadOnlySpan<string> tokens)
    {
        long? ops = null, frames = null;
        int? size = null, minVisitLog2 = null, queryMinVisitLog2 = null;
        double? score = null;
        IlEvmUse? use = null;
        IlEvmCodeShape? shape = null;
        byte[]? code = null;
        foreach (string token in tokens)
        {
            if (Count(token, "ops=") is { } o)
                ops = o;
            else if (Count(token, "frames=") is { } f)
                frames = f;
            else if (Count(token, "size=") is { } s && s <= int.MaxValue)
                size = (int)s;
            else if (token.StartsWith("score=", StringComparison.Ordinal) &&
                     double.TryParse(token.AsSpan("score=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                score = value;
            else if (token.StartsWith("use=", StringComparison.Ordinal))
                use = Use(token.AsSpan("use=".Length));
            else if (Count(token, "minv=") is { } m && m is > 0 and < 64)
                minVisitLog2 = (int)m;
            else if (Count(token, "qminv=") is { } q && q < 64)
                queryMinVisitLog2 = (int)q;
            else if (token.StartsWith("L=", StringComparison.Ordinal))
                shape = Shape(token.AsSpan("L=".Length));
            else if (token.StartsWith("code=", StringComparison.Ordinal))
                code = Code(token.AsSpan("code=".Length));
        }

        return new Entry(hash, ops, frames, size, score, use, minVisitLog2, shape, queryMinVisitLog2, code);

        static long? Count(string token, string name) =>
            token.StartsWith(name, StringComparison.Ordinal) &&
            long.TryParse(token.AsSpan(name.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

        // Hex digits, with or without 0x, two to a byte; anything else, or nothing, is no code, and the entry is one of the
        // code database's.
        static byte[]? Code(ReadOnlySpan<char> hex)
        {
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];
            if (hex.Length == 0 || (hex.Length & 1) != 0 || hex.ContainsAnyExcept("0123456789abcdefABCDEF"))
                return null;
            return Convert.FromHexString(hex);
        }

        // The letters m and w, in any order; - or nothing for neither.
        static IlEvmUse Use(ReadOnlySpan<char> letters)
        {
            IlEvmUse use = IlEvmUse.None;
            foreach (char letter in letters)
            {
                if (letter is 'm' or 'M')
                    use |= IlEvmUse.Main;
                else if (letter is 'w' or 'W')
                    use |= IlEvmUse.Workers;
            }

            return use;
        }

        static IlEvmCodeShape? Shape(ReadOnlySpan<char> name) =>
            name.Equals("line", StringComparison.OrdinalIgnoreCase) ? IlEvmCodeShape.Line :
            name.Equals("mixed", StringComparison.OrdinalIgnoreCase) ? IlEvmCodeShape.Mixed :
            name.Equals("loop", StringComparison.OrdinalIgnoreCase) ? IlEvmCodeShape.Loop : null;
    }

    /// <summary>The entries whose profile ran at least <paramref name="minOpsPerFrame"/> opcodes per frame, in order; those without the counts are kept.</summary>
    /// <param name="skipped">How many entries were left out.</param>
    public static List<Entry> WithMinOpsPerFrame(List<Entry> entries, int minOpsPerFrame, out int skipped)
    {
        List<Entry> kept = minOpsPerFrame <= 0 ? entries : [.. entries.Where(e => e.OpsPerFrame is not { } perFrame || perFrame >= minOpsPerFrame)];
        skipped = entries.Count - kept.Count;
        return kept;
    }
}

/// <summary>Compiles the shipped contracts before blocks are processed or calls served, and arms the profile's and the stats' dumps.</summary>
/// <remarks>
/// <para>
/// Runs after the blockchain is initialized, so the code database, the head and the specs are there, and before the
/// block processor starts and the RPC modules (the Engine API among them) are registered: a client is not ready for
/// payloads or calls until the compile is done, and none of it competes with block processing.
/// </para>
/// <para>
/// Under the Adaptive rules the list compiles at every startup, whatever the scope, the source of live profiling or the
/// kind of node, and at genesis too, for the spec of the wall clock; the compile stops at
/// <see cref="IIlEvmConfig.StartupCompileSeconds"/> and leaves the rest to the inter-block windows; entries whose code
/// the code database lacks are kept for a retry once the node has synced it (<see cref="IlShippedBacklog"/>). An RPC list
/// (<see cref="IlEvmPrecompileList.LoadRpcList"/>, none shipped) would compile first, from the code its entries carry; list
/// entries that run every visit compiled get an ahead copy for the node's first calls (<see cref="IlWarmUp.ArmAhead"/>).
/// The Legacy and measurement rules compile as they did: the block list alone, for the spec 12 seconds after the head,
/// and nothing at genesis.
/// </para>
/// <para>
/// Under the Simple rules the block list's first <see cref="IIlEvmConfig.ListSize"/> entries compile, whole, with no
/// deadline and no RPC list, for the spec the Adaptive rules take, at genesis too; then the live promotion rounds start
/// (<see cref="IlSimple"/>), which also retry the entries the code database lacked.
/// </para>
/// <para>
/// Under the Adaptive rules the step also attaches the inter-block scheduler (<see cref="IlScheduler"/>) to block
/// processing's events, before the compile, with live profiling's scorer (<see cref="IlLive"/>) on its maintenance;
/// compiles ahead what a frame part may call (<see cref="IlWarmUp"/>); and starts the scheduler's workers once the
/// compile is done.
/// </para>
/// </remarks>
/// <param name="mainProcessingContext">Block processing, whose blocks the stats count and whose branches the scheduler's windows fall between; resolved only for <see cref="IlEvmSettings.Stats"/> and the Adaptive rules.</param>
/// <param name="blocksConfig">Block processing's settings: the slot length the spec of the next block is taken at and the scheduler's guard time at the tip, and the pre-warm handoff the stats warn about.</param>
/// <param name="chainHeadInfo">Whether the node syncs, during which the scheduler opens no window; the block tree tells, without it.</param>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockchain)], dependents: [typeof(StartBlockProcessor), typeof(RegisterRpcModules)])]
public sealed class IlEvmStartupStep(
    IIlEvmConfig config,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    Lazy<IMainProcessingContext> mainProcessingContext,
    IBlocksConfig blocksConfig,
    IProcessExitSource processExit,
    ILogManager logManager,
    IChainHeadInfoProvider? chainHeadInfo = null) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<IlEvmStartupStep>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (!IlEvmSettings.Enabled)
            return Task.CompletedTask;

        // What ran compiled, once the node stops: in a benchmark's log, next to its numbers.
        processExit.Token.Register(LogSummary);
        if (_logger.IsInfo)
            _logger.Info($"IL-EVM: mode {IlEvmSettings.Mode}, scope {IlEvmSettings.Scope}, read-only query surfaces {DescribeSurfaces()}; the exit line counts each surface's frame parts offered and run compiled");
        if (IlEvmSettings.ScopeForcedToAll && _logger.IsWarn)
            _logger.Warn("IL-EVM: Prefer=Blocks profiles block processing, which needs its runner attached: Scope is All, not the configured Rpc");

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

        // The Simple rules compile the list's first ListSize entries before blocks or calls, then start live promotion.
        if (IlEvmSettings.Mode == IlMode.Simple)
        {
            processExit.Token.Register(IlSimple.Stop);
            if (config.Prefer == IlEvmPrefer.Blocks && _logger.IsWarn)
                _logger.Warn("IL-EVM: the Simple rules promote from the read-only queries only; Prefer=Blocks counts as None, and block processing runs the startup list alone");
            if (IlSimple.Promote && _logger.IsInfo)
                _logger.Info(string.Create(CultureInfo.InvariantCulture,
                    $"IL-EVM live promotion: every {IlSimple.PromoteMs} ms, the read-only queries' codes past {IlSimple.PromoteOps} interpreted opcodes a second, on {IlSimple.CompileThreads} threads, within {IlSimple.BudgetBytes >> 20} MiB"));
            else if (_logger.IsInfo)
                _logger.Info($"IL-EVM live promotion: off (Prefer={config.Prefer}); only the startup list runs compiled");
            return Task.Run(() =>
            {
                try
                {
                    if (config.Precompile)
                        PrecompileSafely(cancellationToken);
                }
                finally
                {
                    IlSimple.Start(_logger, dbProvider?.CodeDb);
                }
            }, cancellationToken);
        }

        // The inter-block windows the Adaptive rules compile in attach before the compile, which leaves them the
        // entries past its deadline; their workers start once it is done, before blocks or calls.
        if (IlEvmSettings.Mode != IlMode.Adaptive)
            return config.Precompile ? Task.Run(() => PrecompileSafely(cancellationToken), cancellationToken) : Task.CompletedTask;

        IlScheduler scheduler = AttachScheduler();
        return Task.Run(() =>
        {
            try
            {
                // What a frame part may call is compiled before any part runs, not on block processing's thread at its
                // first parts.
                PrepareFramePathSafely();
                if (config.Precompile)
                    PrecompileSafely(cancellationToken);
            }
            finally
            {
                scheduler.Start();
            }
        }, cancellationToken);
    }

    /// <summary>Compiles the Adaptive rules' frame path ahead (<see cref="IlWarmUp"/>); a failure leaves it to the first parts, as before.</summary>
    private void PrepareFramePathSafely()
    {
        try
        {
            long started = Stopwatch.GetTimestamp();
            string? missing = IlWarmUp.PrepareFramePath();
            if (_logger.IsInfo)
                _logger.Info(string.Create(CultureInfo.InvariantCulture,
                    $"IL-EVM: the frame path compiled ahead, {IlWarmUp.Prepared} methods in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms, {IlWarmUp.Failed} left to their first call{IlWarmUp.DescribeFailures()}{(missing is null ? "" : $"; {missing}")}"));
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM: compiling the frame path ahead failed; the first frames compile it: {exception}");
        }
    }

    /// <summary>
    /// Attaches the inter-block scheduler to the main processing context's branch events and the block tree's new heads;
    /// a node whose block processing cannot be reached gets one that never sees a block, idle from the start.
    /// </summary>
    private IlScheduler AttachScheduler()
    {
        IBranchProcessor? branchProcessor = null;
        IBlockProcessingQueue? processingQueue = null;
        try
        {
            branchProcessor = mainProcessingContext.Value.BranchProcessor;
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: block processing's events are not available ({exception.Message}); compiling as on a node that processes no blocks");
        }

        try
        {
            // Its blocks' arrivals, before their branches: the steps keep off their sender recovery too.
            processingQueue = branchProcessor is null ? null : mainProcessingContext.Value.BlockProcessingQueue;
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: the processing queue's events are not available ({exception.Message}); busy from each branch's start only");
        }

        IlSchedulerOptions options = IlSchedulerOptions.From(config, blocksConfig);
        IlScheduler scheduler = IlScheduler.Attach(options, branchProcessor, blockTree, chainHeadInfo, specProvider, dbProvider?.CodeDb, _logger, processingQueue: processingQueue);
        // Live profiling scores, promotes and demotes at the windows' maintenance, for the source Prefer picks.
        IlScheduler.ScorerTick = IlLive.Tick;
        processExit.Token.Register(scheduler.Stop);
        if (_logger.IsInfo)
        {
            _logger.Info($"IL-EVM scheduler: compiles between blocks, from {options.QuietMs} ms after a branch until {options.SlotGuardMs} ms before the next is due; " +
                $"threads: {options.CompileThreads} in windows, {scheduler.WorkerCount} while no blocks come");
            _logger.Info(IlLive.Active is { } source
                ? string.Create(CultureInfo.InvariantCulture,
                    $"IL-EVM live profiling: {(source.Block ? "block processing's single-block branches" : "the read-only queries")} feed it (Prefer={IlEvmSettings.Prefer}); " +
                    $"a code is promoted from {source.ThetaUp} ns saved a {(source.Block ? "block" : "second")}, within {IlLive.BudgetBytes >> 20} MiB")
                : $"IL-EVM live profiling: off (Prefer={IlEvmSettings.Prefer}); only the shipped list runs compiled");
        }

        return scheduler;
    }

    /// <summary>Counts blocks as they start, for each code's distinct blocks, and logs the stats at shutdown.</summary>
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
                (config.Scope == IlEvmScope.All ? "in block processing and on the other threads" : $"in read-only queries only, of {DescribeSurfaces()} (block processing runs no runner with Scope=Rpc)") +
                $"; the self-check is off; the {top} contracts with the most time, and every contract that ran compiled, are logged at shutdown, with each surface's parts offered and compiled in the header");

        // Replayed transactions run nothing on block processing: which ones are replayed depends on how far warming got,
        // which compiled code changes, so the block-processing parts of two runs would not be the same parts.
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

    /// <summary>
    /// The exit line: the rules, the scope and the kinds of query environment attached, the parts each surface's runner was
    /// offered and ran compiled (<see cref="IlSurfaceCounts"/>), the startup list, then the rules' own counters.
    /// </summary>
    private void LogSummary()
    {
        if (IlEvmSettings.Mode == IlMode.Simple)
        {
            // The Simple rules keep none of the Adaptive ones' code states or profiles: their own line.
            string simple = string.Create(CultureInfo.InvariantCulture,
                $"IL-EVM at exit: mode Simple, prefer {IlEvmSettings.Prefer}, scope {IlEvmSettings.Scope}, surfaces {DescribeSurfaces()}; {IlSurfaceCounts.Describe()}; " +
                $"list {IlShippedBacklog.Compiled} compiled, {IlShippedBacklog.MissingCount} missing, {IlShippedBacklog.Declined} declined; {IlSimple.Describe()}{IlEvmSettings.DescribeCaught()}");
            if (_logger.IsInfo)
                _logger.Info(simple);
            else
                Console.WriteLine(simple);
            return;
        }

        string common = string.Create(CultureInfo.InvariantCulture,
            $"IL-EVM at exit: mode {IlEvmSettings.Mode}, prefer {IlEvmSettings.Prefer}, scope {IlEvmSettings.Scope}, surfaces {DescribeSurfaces()}; {IlSurfaceCounts.Describe()}; " +
            $"list {IlShippedBacklog.Compiled} compiled, {IlShippedBacklog.MissingCount} missing, {IlShippedBacklog.Declined} declined, {IlShippedBacklog.LeftoverCount} left; " +
            $"code states {IlRuntime.StatesResolved} resolved, {IlRuntime.StatesRefreshed} refreshed, {IlRuntime.StatesAttached} attached, {IlRuntime.ProfileCount} profiles; ");
        string rules;
        if (IlEvmSettings.Mode == IlMode.Adaptive)
        {
            // What live profiling promoted and holds, what the visit gate decided, summed over the threads, the switchback
            // checks' verdicts, the windows between blocks, and what was compiled ahead off the frames' threads.
            rules = IlLive.Describe() + "; " + IlGate.Describe() + "; " + IlCheck.Describe() + "; " + IlScheduler.DescribeCurrent() +
                string.Create(CultureInfo.InvariantCulture, $"; {IlWarmUp.Prepared} methods compiled ahead, {IlWarmUp.Failed} left to their first call; {IlWarmUp.DescribeAhead()}");
        }
        else
        {
            // The Legacy rules' own: the run-time compiles and their self-check, and the compile thread.
            (int kept, int probation, int givenBack) = IlEvm.SelfCheckVerdicts;
            rules = string.Create(CultureInfo.InvariantCulture,
                $"{IlEvm.PrecompiledCount} precompiled, taken by {IlEvm.PrecompiledAttached} code states; " +
                $"{IlEvmSettings.CompiledContracts} compiled at run time, {IlEvmSettings.FailedContracts} declined, {IlEvmSettings.DeferredCompiles} deferred; " +
                $"self-check {kept} kept, {probation} probation, {givenBack} given back; compile thread busy " +
                $"{IlEvmSettings.CompileBusyTicks / (double)Stopwatch.Frequency:F1} s, idle scheduling {IlEvm.IlCompileQueue.IdleScheduling?.ToString() ?? "not started"}");
        }

        string line = common + rules + IlEvmSettings.DescribeCaught();
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
        IlEvmPrecompileList.ListFile file = IlEvmPrecompileList.LoadList(config.PrecompileList, out string origin);
        // The constants the list's Stats runs fitted, for the block runner's gate and the live model.
        IlRuntime.Calibration = file.Calibration;
        IlGate.Calibrate(file.Calibration);
        IlLive.Calibrate(file.Calibration);
        List<IlEvmPrecompileList.Entry> entries = file.Entries;
        int listed = entries.Count;
        entries = IlEvmPrecompileList.WithMinOpsPerFrame(entries, config.PrecompileMinOpsPerFrame, out int belowMin);
        bool simple = IlEvmSettings.Mode == IlMode.Simple;
        int kept = entries.Count;
        if (simple && config.ListSize > 0 && entries.Count > config.ListSize)
            entries = entries[..config.ListSize];
        if (config.PrecompileLimit > 0 && entries.Count > config.PrecompileLimit)
            entries = entries[..config.PrecompileLimit];
        string source = config.PrecompileMinOpsPerFrame > 0
            ? $"list {origin}, {belowMin} of {listed} entries under {config.PrecompileMinOpsPerFrame} opcodes per frame skipped"
            : $"list {origin}";
        if (simple)
            source += $", the first {entries.Count} of {kept} entries (ListSize={config.ListSize})";
        // The Adaptive rules also compile the RPC list, whatever the scope or the source of live profiling, ahead of the
        // block list: a few contracts, each with its code, which calls send as state overrides and no code database holds.
        bool adaptive = IlEvmSettings.Mode == IlMode.Adaptive;
        if (adaptive && config.PrecompileRpc)
        {
            List<IlEvmPrecompileList.Entry> rpc = [.. IlEvmPrecompileList.LoadRpcList(out _).Entries.Where(static e => e.Code is not null)];
            if (rpc.Count > 0)
            {
                HashSet<ValueHash256> carried = [.. rpc.Select(static e => e.Hash)];
                entries = [.. rpc, .. entries.Where(e => !carried.Contains(e.Hash))];
                source += $" and the RPC list, {rpc.Count} entries with their code";
            }
        }

        if (entries.Count == 0)
        {
            if (_logger.IsInfo)
                _logger.Info($"IL-EVM precompile: no shipped contracts ({source})");
            return;
        }

        // The spec of the next block, which the first payload runs on: a slot after the head, so a fork activating at
        // the next block is the one compiled for. A node at genesis is about to sync to a head of today, and the spec
        // of the wall clock is the one the blocks after its sync run; the list compiles for it rather than not at all.
        // The Legacy and measurement rules compile as they did: a slot of 12 seconds, and nothing at genesis.
        // The Simple rules take the spec as the Adaptive ones do.
        BlockHeader? head = blockTree.Head?.Header;
        bool atGenesis = head is null || head.Number == 0;
        bool slotSpec = adaptive || simple;
        if (atGenesis && !slotSpec)
        {
            if (_logger.IsInfo)
                _logger.Info("IL-EVM precompile: skipped, the node is at genesis");
            return;
        }

        ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        IReleaseSpec spec = specProvider.GetSpec(atGenesis
            ? ForkActivation.TimestampOnly(now)
            : new ForkActivation(head!.Number + 1, head.Timestamp + (slotSpec ? blocksConfig.SecondsPerSlot : 12)));
        string target = atGenesis ? $"the spec of the wall clock, {now}, at genesis" : $"the spec of block {head!.Number + 1}";
        if (IlEvm.BodySetFor(spec) is not { } bodies)
        {
            if (_logger.IsWarn)
                _logger.Warn("IL-EVM precompile: the core has no opcode body set for this spec; nothing precompiled");
            return;
        }

        // Under the Adaptive rules the windows between blocks compile what the deadline leaves; the other rules have no
        // such windows, so they compile the whole list before blocks or calls start.
        bool deadline = IlEvmSettings.Mode == IlMode.Adaptive;
        TimeSpan budget = TimeSpan.FromSeconds(Math.Max(0, config.StartupCompileSeconds));
        IDb codeDb = dbProvider.CodeDb;
        IlEvm.IlContract?[] compiled = new IlEvm.IlContract?[entries.Count];
        ConcurrentBag<int> left = [], missing = [];
        int failed = 0, skipped = 0, ahead = 0;
        long ilBytes = 0, codeBytes = 0, aheadBytes = 0;
        Stopwatch wall = Stopwatch.StartNew();
        TimeSpan jitBefore = System.Runtime.JitInfo.GetCompilationTime();
        // One entry at a time, in list order, so under the memory cap and the deadline the busiest are the ones compiled.
        Parallel.ForEach(Partitioner.Create(Enumerable.Range(0, entries.Count), EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken }, index =>
        {
            if (deadline && wall.Elapsed >= budget)
            {
                left.Add(index);
                return;
            }

            // Past the memory cap the rest of the list is skipped; the list is busiest first.
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
            // An entry the list vouches for on every visit serves the first calls from a copy whose helpers are optimized
            // from the start, until they have tiered up on calls (IlWarmUp.SwitchFromAhead); its code is the entry's own,
            // checked against its hash above.
            if (adaptive && IlWarmUp.AheadMaxMs > 0 && entries[index] is { QueryMinVisitLog2: 0, Code: { } carried } &&
                IlEvm.PrecompileAhead(carried, bodies, out _) is { } copy)
            {
                contract.Ahead = copy;
                Interlocked.Increment(ref ahead);
                Interlocked.Add(ref aheadBytes, copy.MemoryBytes);
            }
        });

        List<(IlEvmPrecompileList.Entry Entry, IlEvm.IlContract Contract)> shipped = [];
        for (int index = 0; index < entries.Count; index++)
        {
            if (compiled[index] is { } contract)
                shipped.Add((entries[index], contract));
        }

        if (simple)
        {
            IlSimple.PublishStartup(shipped);
        }
        else
        {
            IlRuntime.PublishShipped(shipped);
            IlWarmUp.ArmAhead(shipped.Select(static s => s.Contract), _logger);
        }

        List<IlEvmPrecompileList.Entry> leftovers = [.. left.Order().Select(i => entries[i])];
        IlShippedBacklog.Record(bodies, codeDb, leftovers, [.. missing.Order().Select(i => entries[i])], shipped.Count, failed);
        // Each entry the deadline left is a job of the windows between blocks, in list order, ahead of every other job.
        int queued = 0;
        foreach (IlEvmPrecompileList.Entry entry in leftovers)
        {
            if (IlScheduler.TryEnqueue(new ShippedLayoutJob(entry)))
                queued++;
        }

        if (queued < leftovers.Count && _logger.IsWarn)
            _logger.Warn($"IL-EVM precompile: {leftovers.Count - queued} of the {leftovers.Count} entries past the deadline were not queued for the windows between blocks, with no scheduler or its queue full; they stay on the interpreter");
        if (_logger.IsInfo)
        {
            string late = deadline ? string.Create(CultureInfo.InvariantCulture, $", {left.Count} past the {budget.TotalSeconds:F0} s deadline, left for the windows between blocks") : "";
            string copies = ahead > 0 ? string.Create(CultureInfo.InvariantCulture, $", {ahead} with an ahead copy for the first calls, {aheadBytes / (1 << 20)} MiB") : "";
            _logger.Info(string.Create(CultureInfo.InvariantCulture,
                $"IL-EVM precompile: {shipped.Count} of {entries.Count} shipped contracts compiled for {target} " +
                $"({source}; {missing.Count} not in the code database, {failed} declined, {skipped} past the {MaxPrecompiledBytes >> 20} MiB cap{late}{copies}" +
                $"), {codeBytes / 1024} KiB of code, {ilBytes / (1 << 20)} MiB of IL and tables, " +
                $"{wall.Elapsed.TotalSeconds:F1} s on {Environment.ProcessorCount} threads, {(System.Runtime.JitInfo.GetCompilationTime() - jitBefore).TotalSeconds:F1} s of JIT"));
        }
    }
}

/// <summary>
/// The list entries the startup compile did not compile: those past its deadline, left in list order for the windows
/// between blocks, and those whose code the code database lacked, left for a retry once the node has synced it.
/// </summary>
/// <remarks>
/// Each entry compiles for the body set the startup compiled for, and is published as the startup publishes; the
/// inter-block scheduler's jobs do it, a step at a time: a <see cref="ShippedLayoutJob"/> per entry left, and a
/// <see cref="RetryMissingJob"/> every ten minutes for the missing ones. The counts here are the whole list's, for the exit line.
/// </remarks>
internal static class IlShippedBacklog
{
    /// <summary>What compiling an entry came to.</summary>
    internal enum Outcome
    {
        Compiled,
        /// <summary>The code database holds no code for the hash.</summary>
        Missing,
        /// <summary>The compiler declined the code, a failure counted as any other.</summary>
        Declined,
    }

    private static readonly object _lock = new();
    private static readonly List<IlEvmPrecompileList.Entry> _leftovers = [];
    private static readonly List<IlEvmPrecompileList.Entry> _missing = [];
    private static OpcodeBodySet? _bodies;
    private static IDb? _codeDb;
    private static int _compiled;
    private static int _declined;

    /// <summary>Entries a job took off the leftovers and has not finished, under the lock.</summary>
    private static int _taken;

    /// <summary>The body set the startup compiled for, which the entries left compile for; null before a startup compile.</summary>
    public static OpcodeBodySet? Bodies
    {
        get
        {
            lock (_lock)
                return _bodies;
        }
    }

    /// <summary>List entries compiled, at startup or since.</summary>
    public static int Compiled => Volatile.Read(ref _compiled);

    /// <summary>List entries the compiler declined.</summary>
    public static int Declined => Volatile.Read(ref _declined);

    /// <summary>Entries the deadline left, not compiled yet: those waiting, and those a job is compiling.</summary>
    public static int LeftoverCount
    {
        get
        {
            lock (_lock)
                return _leftovers.Count + _taken;
        }
    }

    /// <summary>Entries whose code was not in the code database when last tried.</summary>
    public static int MissingCount
    {
        get
        {
            lock (_lock)
                return _missing.Count;
        }
    }

    /// <summary>The entries the deadline left, in list order.</summary>
    public static List<IlEvmPrecompileList.Entry> Leftovers()
    {
        lock (_lock)
            return [.. _leftovers];
    }

    /// <summary>The entries whose code was missing, in list order.</summary>
    public static List<IlEvmPrecompileList.Entry> Missing()
    {
        lock (_lock)
            return [.. _missing];
    }

    /// <summary>Records what a startup compile left, in place of what an earlier one did.</summary>
    internal static void Record(OpcodeBodySet bodies, IDb codeDb, IEnumerable<IlEvmPrecompileList.Entry> leftovers, IEnumerable<IlEvmPrecompileList.Entry> missing, int compiled, int declined)
    {
        lock (_lock)
        {
            _bodies = bodies;
            _codeDb = codeDb;
            _leftovers.Clear();
            _leftovers.AddRange(leftovers);
            _missing.Clear();
            _missing.AddRange(missing);
            Volatile.Write(ref _compiled, compiled);
            Volatile.Write(ref _declined, declined);
        }
    }

    /// <summary>Compiles and publishes the first entry the deadline left: one step of the windows between blocks.</summary>
    /// <returns>Whether there was an entry; one whose code is missing joins the missing ones.</returns>
    public static bool CompileNextLeftover()
    {
        IlEvmPrecompileList.Entry entry;
        OpcodeBodySet bodies;
        IDb codeDb;
        lock (_lock)
        {
            if (_leftovers.Count == 0 || _bodies is null || _codeDb is null)
                return false;
            entry = _leftovers[0];
            _leftovers.RemoveAt(0);
            bodies = _bodies;
            codeDb = _codeDb;
        }

        switch (TryCompile(codeDb, entry, bodies, out IlEvm.IlContract? contract, out _))
        {
            case Outcome.Compiled:
                Interlocked.Increment(ref _compiled);
                IlRuntime.PublishShipped([(entry, contract!)]);
                break;
            case Outcome.Missing:
                lock (_lock)
                    _missing.Add(entry);
                break;
            default:
                Interlocked.Increment(ref _declined);
                break;
        }

        return true;
    }

    /// <summary>Compiles and publishes every entry whose code the code database lacked and now holds; the rest stay missing.</summary>
    /// <returns>How many entries were compiled.</returns>
    public static int RetryMissing()
    {
        List<IlEvmPrecompileList.Entry> missing;
        OpcodeBodySet bodies;
        IDb codeDb;
        lock (_lock)
        {
            if (_missing.Count == 0 || _bodies is null || _codeDb is null)
                return 0;
            missing = [.. _missing];
            bodies = _bodies;
            codeDb = _codeDb;
        }

        List<(IlEvmPrecompileList.Entry Entry, IlEvm.IlContract Contract)> compiled = [];
        HashSet<ValueHash256> found = [];
        foreach (IlEvmPrecompileList.Entry entry in missing)
        {
            Outcome outcome = TryCompile(codeDb, entry, bodies, out IlEvm.IlContract? contract, out _);
            if (outcome == Outcome.Missing)
                continue;
            found.Add(entry.Hash);
            if (outcome == Outcome.Compiled)
                compiled.Add((entry, contract!));
            else
                Interlocked.Increment(ref _declined);
        }

        lock (_lock)
            _missing.RemoveAll(e => found.Contains(e.Hash));
        Interlocked.Add(ref _compiled, compiled.Count);
        if (IlEvmSettings.Mode == IlMode.Simple)
            IlSimple.PublishStartup(compiled);
        else
            IlRuntime.PublishShipped(compiled);
        return compiled.Count;
    }

    /// <summary>
    /// Takes <paramref name="entry"/> off the leftovers for the job that compiles it, with the body set and the code
    /// database it compiles from; false when it is not among them, as when something compiled it meanwhile.
    /// </summary>
    /// <remarks>The job reports how it went with <see cref="Finish"/>; until then the entry still counts as left.</remarks>
    internal static bool Take(IlEvmPrecompileList.Entry entry, [NotNullWhen(true)] out OpcodeBodySet? bodies, [NotNullWhen(true)] out IDb? codeDb)
    {
        lock (_lock)
        {
            bodies = _bodies;
            codeDb = _codeDb;
            int at = IndexOf(_leftovers, entry);
            if (at < 0 || bodies is null || codeDb is null)
            {
                bodies = null;
                codeDb = null;
                return false;
            }

            _leftovers.RemoveAt(at);
            _taken++;
            return true;
        }
    }

    /// <summary>A job is done with the entry it took: compiled, then published as the startup compile publishes; missing from the code database, then kept for a retry; or declined.</summary>
    internal static void Finish(IlEvmPrecompileList.Entry entry, Outcome outcome, IlEvm.IlContract? contract)
    {
        lock (_lock)
        {
            _taken = Math.Max(0, _taken - 1);
            if (outcome == Outcome.Missing)
                _missing.Add(entry);
        }

        if (outcome == Outcome.Compiled)
        {
            Interlocked.Increment(ref _compiled);
            IlRuntime.PublishShipped([(entry, contract!)]);
        }
        else if (outcome == Outcome.Declined)
        {
            Interlocked.Increment(ref _declined);
        }
    }

    /// <summary>Moves a missing entry back to the leftovers once the code database holds its code, for a job to compile; false while it does not.</summary>
    internal static bool Found(IlEvmPrecompileList.Entry entry)
    {
        IDb? codeDb;
        lock (_lock)
            codeDb = _codeDb;
        // Probed outside the lock: a read of the database may wait on its disk.
        if (codeDb is null || !codeDb.KeyExists(entry.Hash.Bytes))
            return false;
        lock (_lock)
        {
            int at = IndexOf(_missing, entry);
            if (at < 0)
                return false;
            _missing.RemoveAt(at);
            _leftovers.Add(entry);
            return true;
        }
    }

    private static int IndexOf(List<IlEvmPrecompileList.Entry> entries, IlEvmPrecompileList.Entry entry)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Hash == entry.Hash)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The code <paramref name="entry"/> compiles from: the code it carries, once its keccak is the entry's hash, else the
    /// code database's for the hash; null when neither has it.
    /// </summary>
    /// <param name="mismatched">Whether the entry carries code whose keccak is not its hash, which never compiles: a shipped contract runs wherever its keccak is met.</param>
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

    /// <summary>The reason a declined entry is counted under when its code is not its hash's.</summary>
    internal const string MismatchedCode = "the list's code does not match its hash";

    /// <summary>
    /// Compiles the code of <paramref name="entry"/>, the code it carries or else the code database's, for
    /// <paramref name="bodies"/>, every chunk at once; a failure counts as any compiler failure.
    /// </summary>
    /// <param name="codeLength">The code's length, or 0 when it is missing.</param>
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

    /// <summary>Forgets what was recorded, for tests.</summary>
    internal static void Reset()
    {
        lock (_lock)
        {
            _bodies = null;
            _codeDb = null;
            _leftovers.Clear();
            _missing.Clear();
            _taken = 0;
            Volatile.Write(ref _compiled, 0);
            Volatile.Write(ref _declined, 0);
        }
    }
}
