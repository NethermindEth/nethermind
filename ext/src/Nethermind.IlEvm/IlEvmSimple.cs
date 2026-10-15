// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Logging;

namespace Nethermind.IlEvm;

/// <summary>The sampled counts of one profile, striped by thread so the threads that run one hot code write separate cache lines.</summary>
/// <remarks>Made on a profile's first sampled part, so the many codes met once and never sampled carry none.</remarks>
internal sealed class IlSimpleCounts
{
    /// <summary>The stripes; a thread writes the one its <see cref="IlSimpleThread.Stripe"/> names.</summary>
    public const int Stripes = 4;

    /// <summary>One stripe's counts, a cache line of its own.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct Stripe
    {
        [FieldOffset(0)] public long Parts;
        [FieldOffset(8)] public long Ops;
        [FieldOffset(16)] public long CompiledParts;
    }

    // One stripe of padding first, so the first one written shares no line with the array's header.
    private readonly Stripe[] _stripes = new Stripe[Stripes + 1];

    /// <summary>A sampled interpreted part. Plain writes: mostly one thread writes a stripe, and a lost count only moves a rate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddPart(int stripe) => _stripes[stripe + 1].Parts++;

    /// <summary>The interpreted opcodes of a sampled part.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddOps(int stripe, int ops) => _stripes[stripe + 1].Ops += ops;

    /// <summary>A sampled compiled part of the profile's promoted contract.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddCompiled(int stripe) => _stripes[stripe + 1].CompiledParts++;

    /// <summary>The counts summed over the stripes, and cleared when <paramref name="take"/>.</summary>
    public (long Parts, long Ops, long CompiledParts) Sum(bool take)
    {
        long parts = 0, ops = 0, compiled = 0;
        for (int i = 1; i <= Stripes; i++)
        {
            ref Stripe stripe = ref _stripes[i];
            parts += take ? Interlocked.Exchange(ref stripe.Parts, 0) : Volatile.Read(ref stripe.Parts);
            ops += take ? Interlocked.Exchange(ref stripe.Ops, 0) : Volatile.Read(ref stripe.Ops);
            compiled += take ? Interlocked.Exchange(ref stripe.CompiledParts, 0) : Volatile.Read(ref stripe.CompiledParts);
        }

        return (parts, ops, compiled);
    }
}

/// <summary>What the Simple rules keep of one code content, whichever code infos carry it: its compiled contracts and live profiling's counts.</summary>
/// <remarks>
/// Keyed by the keccak of code a cache stamped, else by the code's bytes, which a content profile keeps
/// (<see cref="Bytes"/>): code no cache stamped, as a state override's, is found by its length and bytes, never hashed.
/// The counts are written racily from the frames' threads, a sampled part at a time, into per-thread stripes
/// (<see cref="IlSimpleCounts"/>); a lost update only moves a rate.
/// </remarks>
internal sealed class IlSimpleProfile
{
    /// <summary>The stamped keccak, or default for a profile found by content.</summary>
    public readonly ValueHash256 Hash;

    /// <summary>The code's length in bytes.</summary>
    public readonly int Length;

    /// <summary>For a profile in the content index, the code it matches, byte for byte: the startup or promoted contract's code, or the bytes kept for a live compile; empty for one found by hash alone.</summary>
    public ReadOnlyMemory<byte> Bytes;

    /// <summary>For stamped code, a code info of it, which a live compile reads the code from; the code database's copy where it was collected.</summary>
    public WeakReference<CodeInfo>? Source;

    /// <summary>The contract the startup list compiled, for the body set the node started on; never released.</summary>
    public volatile IlEvm.IlContract? Startup;

    /// <summary>The contract live promotion compiled, for the body set of its time; null until then, and once evicted. A promotion for a newer fork replaces it.</summary>
    public volatile IlEvm.IlContract? Promoted;

    /// <summary>The body set of the latest sampled part, the one a live compile is for.</summary>
    public OpcodeBodySet? Bodies;

    private IlSimpleCounts? _counts;

    /// <summary>Interpreted parts per round, scaled back from the sample and halved each round: what a newcomer weighs against the promoted contracts it would evict.</summary>
    public double Demand;

    /// <summary>The promoted contract's compiled parts per round, scaled back from the sample and halved each round, seeded with the demand it was promoted on: the eviction's order.</summary>
    public double Usage;

    /// <summary>Rounds in a row with nothing counted, for pruning.</summary>
    public int IdleRounds;

    /// <summary>The compiler declined the code, or its code could not be had: never counted or tried again.</summary>
    public volatile bool Declined;

    /// <summary>The round before which a contract the budget turned away is not compiled again.</summary>
    public long RetryRound;

    /// <summary>The size of the last contract the budget turned away, which the next try checks the budget against before compiling; 0 for none.</summary>
    public long RejectedBytes;

    /// <summary>Whether the profile is in the content index, as every content profile is until retired, and a stamped one once promoted or compiled at startup.</summary>
    public bool InContentIndex;

    /// <summary>A content profile taken out of the index, pruned or merged into a stamped twin; states still counting into it resolve again at the next generation.</summary>
    public bool Retired;

    public IlSimpleProfile(in ValueHash256 hash, int length)
    {
        Hash = hash;
        Length = length;
    }

    /// <summary>Whether the profile was found by content, so a code info's bytes must match before its state takes a contract.</summary>
    public bool ByContent => Hash == default;

    /// <summary>The sampled counts, made on the first sampled part.</summary>
    public IlSimpleCounts Counts
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _counts ?? CreateCounts();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IlSimpleCounts CreateCounts()
    {
        Interlocked.CompareExchange(ref _counts, new IlSimpleCounts(), null);
        return _counts!;
    }

    /// <summary>Takes the counts since the last round, clearing them.</summary>
    public (long Parts, long Ops, long CompiledParts) Take() => _counts?.Sum(take: true) ?? default;

    /// <summary>The sampled interpreted parts since the last round, for tests.</summary>
    internal long Parts => _counts?.Sum(take: false).Parts ?? 0;

    /// <summary>Their opcodes, for tests.</summary>
    internal long Ops => _counts?.Sum(take: false).Ops ?? 0;

    /// <summary>The sampled compiled parts since the last round, for tests.</summary>
    internal long CompiledParts => _counts?.Sum(take: false).CompiledParts ?? 0;

    /// <summary>The contract a runner runs the code on for <paramref name="bodies"/>: the startup one, else, for the queries' runner, the promoted one; null for none.</summary>
    public IlEvm.IlContract? RunFor(OpcodeBodySet bodies, bool block)
    {
        IlEvm.IlContract? startup = Startup;
        if (startup is not null && ReferenceEquals(startup.Bodies, bodies))
            return startup;
        if (block)
            return null;
        IlEvm.IlContract? promoted = Promoted;
        return promoted is not null && ReferenceEquals(promoted.Bodies, bodies) ? promoted : null;
    }

    /// <summary>Whether the profile holds a contract for any body set.</summary>
    public bool HoldsAny => Startup is not null || Promoted is not null;

    /// <summary>A short name for the logs: the first bytes of the keccak, or, for code found by content, its length and a hash of its bytes, taken off the frame path.</summary>
    public string ShortName => ByContent
        ? string.Create(CultureInfo.InvariantCulture, $"c{Length}-{(uint)((ReadOnlySpan<byte>)Bytes.Span).FastHash():x8}")
        : Hash.ToString()[..10];
}

/// <summary>The Simple rules' state of one code info, in the frame code runner's per-code slot.</summary>
/// <remarks>Resolved once per code info, <see cref="IlSimple.Generation"/> and body set: every part reads the slot, its type, the generation, the body set and one contract.</remarks>
internal sealed class IlSimpleState
{
    /// <summary>What the read-only queries' runner runs the code on, or null for the interpreter.</summary>
    public IlEvm.IlContract? Query;

    /// <summary>What block processing's runner runs the code on (Scope=All): a startup contract, or null.</summary>
    public IlEvm.IlContract? Block;

    /// <summary>The profile the queries' runner counts the code's interpreted parts into, sampled; null when it has a contract or counts nothing.</summary>
    public IlSimpleProfile? Count;

    /// <summary>The profile of a promoted <see cref="Query"/>, whose compiled parts are sampled for eviction; null for a startup contract.</summary>
    public IlSimpleProfile? Uses;

    /// <summary>The body set the state was resolved for; a part on another, as a call at an older fork's block, resolves again.</summary>
    public OpcodeBodySet? Bodies;

    /// <summary>The <see cref="IlSimple.Generation"/> the state was resolved at; at a newer one it resolves again.</summary>
    public int Generation;
}

/// <summary>A code slot's mark that neither runner has anything compiled for the code, for any body set, and that the queries' runner counts nothing of it: a part is declined on the mark alone.</summary>
/// <remarks>Replaced at every new generation, so a mark of an older one resolves once more.</remarks>
internal sealed class IlSimpleMark(string name)
{
    public override string ToString() => name;
}

/// <summary>Per-thread state of the Simple rules' runners: the parts each was offered, summed for the exit line, the sample's random numbers and the stripe of the counts; one thread writes each.</summary>
internal sealed class IlSimpleThread
{
    [ThreadStatic] private static IlSimpleThread? _current;
    private static readonly List<IlSimpleThread> _all = [];

    /// <summary>Parts each runner ran compiled and left to the interpreter.</summary>
    public long QueryCompiled, QueryInterpreted, BlockCompiled, BlockInterpreted;

    /// <summary>The stripe of the profiles' counts this thread writes.</summary>
    public readonly int Stripe;

    private uint _random;

    private IlSimpleThread(int index)
    {
        Stripe = index & (IlSimpleCounts.Stripes - 1);
        // Any nonzero seed that differs between threads: xorshift never leaves a nonzero state.
        _random = ((uint)Environment.CurrentManagedThreadId * 0x9E3779B9u ^ (uint)Stopwatch.GetTimestamp()) | 1u;
    }

    public static IlSimpleThread Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current ?? Create();
    }

    /// <summary>
    /// The thread's next xorshift number. The sample draws on it rather than taking every nth part: a thread that repeats
    /// one call pattern would sample the same parts of it every time, and the others never.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Next()
    {
        uint x = _random;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _random = x;
        return x;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IlSimpleThread Create()
    {
        IlSimpleThread created;
        lock (_all)
        {
            created = new IlSimpleThread(_all.Count);
            _all.Add(created);
        }

        _current = created;
        return created;
    }

    /// <summary>The counts summed over every thread that ran a part.</summary>
    public static (long QueryCompiled, long QueryInterpreted, long BlockCompiled, long BlockInterpreted) Totals()
    {
        long qc = 0, qi = 0, bc = 0, bi = 0;
        lock (_all)
        {
            foreach (IlSimpleThread thread in _all)
            {
                qc += Volatile.Read(ref thread.QueryCompiled);
                qi += Volatile.Read(ref thread.QueryInterpreted);
                bc += Volatile.Read(ref thread.BlockCompiled);
                bi += Volatile.Read(ref thread.BlockInterpreted);
            }
        }

        return (qc, qi, bc, bi);
    }
}

/// <summary>
/// The Simple rules (<see cref="IlMode.Simple"/>): the startup list compiled at startup, and what live promotion adds from
/// the read-only queries' own load; every frame part of a contract in the compiled set runs compiled, with no gate, check
/// or probation.
/// </summary>
/// <remarks>
/// <para>
/// Lookup: a code info's slot holds an <see cref="IlSimpleState"/> naming each runner's contract for one body set, or a
/// mark where neither runner has anything to run or count. Stamped code is found by its keccak, code no cache stamped by
/// its length and bytes; stamped code whose own profile has nothing compiled for the body set is also looked for by its
/// bytes, so one contract serves the database's code and override code of the same bytes. Every change to the compiled
/// set moves <see cref="Generation"/>, and the states and marks of older ones resolve again.
/// </para>
/// <para>
/// Live promotion (<see cref="IlEvmPrefer.Rpc"/>): the queries' runner counts one part in <see cref="SampleEvery"/> of
/// each code not compiled, drawn at random per thread, with its opcodes. Every <see cref="PromoteMs"/> a background round
/// takes the codes whose interpreted opcodes per second, scaled back from the sample, reach <see cref="PromoteOps"/>,
/// busiest first, compiles them on <see cref="CompileThreads"/> threads at the lowest priority, and adds them to the
/// compiled set within <see cref="BudgetBytes"/>, evicting the least used promoted contracts used less than the newcomer
/// is in demand to make room. The startup contracts are never evicted, and nothing is demoted for its speed. A code
/// promoted for one fork is promoted again for the next, its old contract replaced.
/// </para>
/// </remarks>
internal static class IlSimple
{
    /// <summary>One part in this many of a code not compiled is counted, on average, with this weight.</summary>
    public const int SampleEvery = 8;

    private const uint SampleMask = SampleEvery - 1;

    /// <summary>The most codes promoted in one round, busiest first; the rest wait for the next.</summary>
    public const int MaxPerRound = 32;

    /// <summary>The most content profiles of one code length, so a flood of distinct override codes stays cheap to match.</summary>
    public const int MaxPerLength = 16;

    /// <summary>Rounds a contract the budget turned away waits before it is tried again.</summary>
    public const int BudgetRetryRounds = 12;

    /// <summary>Rounds in a row with nothing counted after which a profile holding nothing may be pruned.</summary>
    public const int IdleToPrune = 3;

    /// <summary>How often the startup list's entries the code database lacked are looked for again, in milliseconds.</summary>
    public const long RetryMissingMs = 10 * 60 * 1000;

    /// <summary>Whether the queries' runner counts for live promotion: <see cref="IlEvmPrefer.Rpc"/>.</summary>
    public static bool Promote { get; set; } = true;

    /// <summary>The time between promotion rounds, in milliseconds.</summary>
    public static int PromoteMs { get; set; } = 5_000;

    /// <summary>The interpreted opcodes per second a code must run to be promoted.</summary>
    public static long PromoteOps { get; set; } = 200_000;

    /// <summary>The threads a round compiles on.</summary>
    public static int CompileThreads { get; set; } = 1;

    /// <summary>The most IL and tables the promoted contracts may hold together.</summary>
    public static long BudgetBytes { get; set; } = 128L << 20;

    /// <summary>The most profiles kept, startup ones included; past it, code met gets none and is not counted.</summary>
    public static int MaxProfiles { get; set; } = 32_768;

    /// <summary>The most bytes of code no cache stamped the content profiles keep for live compiles.</summary>
    public static long MaxRetainedBytes { get; set; } = 16L << 20;

    /// <summary>For tests: <see cref="Start"/> starts no thread, and the test runs the rounds itself.</summary>
    internal static bool ManualForTests { get; set; }

    /// <summary>Moves on every change to the compiled set; states and marks of an older one resolve again.</summary>
    public static int Generation = 1;

    /// <summary>The mark of code neither runner has anything for, which the queries' runner does not count.</summary>
    public static IlSimpleMark Nothing = new("nothing");

    /// <summary>The mark of code block processing's runner has nothing for, made where it met code first; the queries' runner resolves it for itself.</summary>
    public static IlSimpleMark NothingBlock = new("nothing: block");

    private static readonly object _lock = new();
    private static readonly ConcurrentDictionary<ValueHash256, IlSimpleProfile> _byHash = new();

    /// <summary>The content index: the content profiles, and the stamped ones promoted or compiled at startup, by code length; copied on write.</summary>
    private static Dictionary<int, IlSimpleProfile[]> _byLength = [];

    /// <summary>The content profiles, for the rounds, which also walk <see cref="_byHash"/>; copied on write.</summary>
    private static IlSimpleProfile[] _content = [];

    /// <summary>The profiles holding a promoted contract, under the lock.</summary>
    private static readonly List<IlSimpleProfile> _promoted = [];

    private static int _profiles, _startup, _promotedTotal, _evicted, _rejected, _declined, _rounds, _promotionRounds, _compiles;
    private static long _retainedBytes, _usedBytes, _startupBytes, _resolved, _round, _lastMissingRetry;
    private static long _lastRoundTicks;

    /// <summary>Set when a content profile was refused for a full length or the retained bytes' cap: the next round prunes the idle ones.</summary>
    private static volatile bool _contentFull;

    private static Thread? _thread;
    private static volatile bool _stopping;
    private static readonly AutoResetEvent _wake = new(false);
    private static ILogger _logger;
    private static IDb? _codeDb;

    /// <summary>Contracts compiled from the startup list.</summary>
    public static int StartupCount => Volatile.Read(ref _startup);

    /// <summary>Promoted contracts held now.</summary>
    public static int PromotedCount
    {
        get
        {
            lock (_lock)
                return _promoted.Count;
        }
    }

    /// <summary>Promotions so far, evicted ones included; evictions; candidates the budget turned away; codes the compiler declined.</summary>
    public static (int Promoted, int Evicted, int Rejected, int Declined) Totals =>
        (Volatile.Read(ref _promotedTotal), Volatile.Read(ref _evicted), Volatile.Read(ref _rejected), Volatile.Read(ref _declined));

    /// <summary>The IL and tables the promoted contracts hold, against <see cref="BudgetBytes"/>.</summary>
    public static long UsedBytes => Interlocked.Read(ref _usedBytes);

    /// <summary>The bytes of code the content profiles keep, against <see cref="MaxRetainedBytes"/>.</summary>
    public static long RetainedBytes => Interlocked.Read(ref _retainedBytes);

    /// <summary>The profiles kept.</summary>
    public static int ProfileCount => Volatile.Read(ref _profiles);

    /// <summary>States resolved: once per code info, generation and body set.</summary>
    public static long Resolved => Interlocked.Read(ref _resolved);

    /// <summary>Promotion rounds run, and those that promoted anything.</summary>
    public static (int Rounds, int Promoting) Rounds => (Volatile.Read(ref _rounds), Volatile.Read(ref _promotionRounds));

    /// <summary>Contracts the rounds compiled, those the budget then turned away included; for tests.</summary>
    internal static int Compiles => Volatile.Read(ref _compiles);

    /// <summary>The profile of stamped code, for tests.</summary>
    internal static IlSimpleProfile? ProfileOf(in ValueHash256 hash) => _byHash.TryGetValue(hash, out IlSimpleProfile? profile) ? profile : null;

    /// <summary>The profile of code found by content, for tests.</summary>
    internal static IlSimpleProfile? ProfileOf(ReadOnlySpan<byte> code) => FindByContent(code);

    /// <summary>
    /// Resolves a code info whose slot holds no current state for <paramref name="bodies"/>: <paramref name="seen"/>,
    /// nothing, a mark, or a state of an older generation or another body set. Installs the state, or the mark when
    /// neither runner has anything to run or count.
    /// </summary>
    /// <returns>The state, or null for a part the runner declines.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IlSimpleState? Resolve(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies, object? seen, bool block)
    {
        // The generation and the marks first: a change publishes what changed before it moves them, so a state made here
        // is never newer than what the lookup sees.
        int generation = Volatile.Read(ref Generation);
        IlSimpleMark nothing = Volatile.Read(ref Nothing);
        IlSimpleMark nothingBlock = Volatile.Read(ref NothingBlock);
        // Initcode is never in the code database, and nothing compiles it.
        if (vm.VmState.ExecutionType.IsAnyCreate())
            return Mark(codeInfo, seen, nothing);

        // Stamped code by its keccak. Code no cache stamped, and stamped code whose own profile has nothing for these
        // bodies, by its length and bytes: a contract compiled from a state override's bytes, or a startup entry's,
        // serves the same code from the database, and the other way round.
        ValueHash256 hash = codeInfo.CodeHash;
        IlSimpleProfile? profile = hash != default && _byHash.TryGetValue(hash, out IlSimpleProfile? stamped) ? stamped : null;
        if (profile is null || profile.RunFor(bodies, block: false) is null)
        {
            IlSimpleProfile? twin = FindByContent(codeInfo.CodeSpan);
            if (twin is not null && (profile is null || twin.RunFor(bodies, block: false) is not null))
                profile = twin;
        }

        // Only the queries' runner counts, so only it makes profiles.
        bool counting = Promote && !block;
        if (profile is null && counting)
            profile = Create(codeInfo, hash);
        else if (counting && profile is { ByContent: false, Bytes.IsEmpty: true } && profile.Source?.TryGetTarget(out _) != true)
            // The code info a live compile reads the code from was collected: this one, of the same keccak, serves instead.
            profile.Source = new WeakReference<CodeInfo>(codeInfo);
        if (profile is null)
            return Mark(codeInfo, seen, block && Promote ? nothingBlock : nothing);

        IlEvm.IlContract? query = profile.RunFor(bodies, block: false);
        IlEvm.IlContract? blockRun = profile.RunFor(bodies, block: true);
        IlSimpleProfile? count = query is null && Promote && !profile.Declined ? profile : null;
        // The mark serves every body set, so it is only for code with nothing compiled for any; code compiled for another
        // fork than these bodies' keeps a state for these, and a part on the other resolves again.
        if (query is null && blockRun is null && count is null && !profile.HoldsAny)
            return Mark(codeInfo, seen, nothing);

        IlSimpleState state = new()
        {
            Query = query,
            Block = blockRun,
            Count = count,
            Uses = query is not null && ReferenceEquals(query, profile.Promoted) ? profile : null,
            Bodies = bodies,
            Generation = generation,
        };
        Interlocked.Increment(ref _resolved);
        return Install(codeInfo, seen, state);
    }

    /// <summary>Among the content index's profiles as long as <paramref name="code"/>, the one whose bytes equal it; stops at the first difference, mostly within the first words.</summary>
    /// <remarks>The index holds one profile per content: adding one retires any content profile of the same bytes.</remarks>
    private static IlSimpleProfile? FindByContent(ReadOnlySpan<byte> code)
    {
        if (!Volatile.Read(ref _byLength).TryGetValue(code.Length, out IlSimpleProfile[]? candidates))
            return null;
        foreach (IlSimpleProfile candidate in candidates)
        {
            if (code.SequenceEqual(candidate.Bytes.Span))
                return candidate;
        }

        return null;
    }

    /// <summary>Whether a content profile of <paramref name="length"/> bytes would be refused: its length's slots, or the retained bytes, are full.</summary>
    private static bool ContentFull(int length)
    {
        if (Interlocked.Read(ref _retainedBytes) + length > MaxRetainedBytes)
            return true;
        if (!Volatile.Read(ref _byLength).TryGetValue(length, out IlSimpleProfile[]? known))
            return false;
        int content = 0;
        foreach (IlSimpleProfile profile in known)
        {
            if (profile.ByContent)
                content++;
        }

        return content >= MaxPerLength;
    }

    /// <summary>A profile for code the queries' runner met, unless the table, or for content profiles their length's slots or the retained bytes, are full.</summary>
    private static IlSimpleProfile? Create(CodeInfo codeInfo, in ValueHash256 hash)
    {
        if (Volatile.Read(ref _profiles) >= MaxProfiles)
            return null;
        int length = codeInfo.CodeSpan.Length;
        if (hash != default)
        {
            IlSimpleProfile created = new(hash, length) { Source = new WeakReference<CodeInfo>(codeInfo) };
            IlSimpleProfile profile = _byHash.GetOrAdd(hash, created);
            if (ReferenceEquals(profile, created))
                Interlocked.Increment(ref _profiles);
            return profile;
        }

        // Refused without the lock: override code infos are new per call, so a full cap is met on every call that
        // carries such code until the next round prunes the idle profiles.
        if (ContentFull(length))
        {
            _contentFull = true;
            return null;
        }

        lock (_lock)
        {
            // Another thread may have made it meanwhile.
            if (FindByContent(codeInfo.CodeSpan) is { } found)
                return found;
            if (ContentFull(length))
            {
                _contentFull = true;
                return null;
            }

            IlSimpleProfile profile = new(default, length) { Bytes = codeInfo.Code };
            Interlocked.Add(ref _retainedBytes, length);
            AddToIndex(profile, evicted: null);
            Volatile.Write(ref _content, [.. _content, profile]);
            Interlocked.Increment(ref _profiles);
            return profile;
        }
    }

    /// <summary>
    /// Adds <paramref name="profile"/> to the content index, under the lock. A content profile of the same bytes is
    /// retired, its promoted contract evicted into <paramref name="evicted"/>, so one profile compiles and holds the code.
    /// </summary>
    private static void AddToIndex(IlSimpleProfile profile, List<IlSimpleProfile>? evicted)
    {
        IlSimpleProfile[] known = _byLength.TryGetValue(profile.Length, out IlSimpleProfile[]? bucket) ? bucket : [];
        HashSet<IlSimpleProfile>? twins = null;
        foreach (IlSimpleProfile other in known)
        {
            if (!ReferenceEquals(other, profile) && other.ByContent && other.Bytes.Span.SequenceEqual(profile.Bytes.Span))
                (twins ??= []).Add(other);
        }

        if (twins is not null)
            Remove(twins, evicted);
        Dictionary<int, IlSimpleProfile[]> index = new(_byLength);
        IlSimpleProfile[] candidates = index.TryGetValue(profile.Length, out IlSimpleProfile[]? current) ? current : [];
        index[profile.Length] = [.. candidates, profile];
        profile.InContentIndex = true;
        Volatile.Write(ref _byLength, index);
    }

    /// <summary>
    /// Takes <paramref name="gone"/> out of the content index, under the lock: their promoted contracts are evicted, and
    /// the content profiles among them retired, with their kept bytes given back; stamped ones stay in the keccak table.
    /// </summary>
    private static void Remove(HashSet<IlSimpleProfile> gone, List<IlSimpleProfile>? evicted)
    {
        Dictionary<int, IlSimpleProfile[]> index = new(_byLength);
        foreach (int length in gone.Select(static p => p.Length).Distinct().ToList())
        {
            if (!index.TryGetValue(length, out IlSimpleProfile[]? bucket))
                continue;
            IlSimpleProfile[] kept = bucket.Where(p => !gone.Contains(p)).ToArray();
            if (kept.Length == 0)
                index.Remove(length);
            else
                index[length] = kept;
        }

        Volatile.Write(ref _byLength, index);
        int retired = 0;
        foreach (IlSimpleProfile profile in gone)
        {
            if (profile.Promoted is not null)
            {
                Evict(profile);
                evicted?.Add(profile);
            }

            profile.InContentIndex = false;
            if (profile.ByContent && !profile.Retired)
            {
                profile.Retired = true;
                Interlocked.Add(ref _retainedBytes, -profile.Length);
                retired++;
            }
        }

        if (retired > 0)
        {
            Volatile.Write(ref _content, _content.Where(p => !gone.Contains(p)).ToArray());
            Interlocked.Add(ref _profiles, -retired);
        }
    }

    /// <summary>Marks the slot over nothing, a mark or an old state, and declines the part.</summary>
    private static IlSimpleState? Mark(CodeInfo codeInfo, object? seen, IlSimpleMark mark)
    {
        if (!ReferenceEquals(seen, mark) && Replaceable(seen))
            Interlocked.CompareExchange(ref IlEvmFrameRunner.CodeSlot(codeInfo), mark, seen);
        return null;
    }

    /// <summary>Installs <paramref name="state"/> over <paramref name="seen"/>; another thread's state, installed first, serves the part instead when it is current for the same bodies.</summary>
    private static IlSimpleState Install(CodeInfo codeInfo, object? seen, IlSimpleState state)
    {
        // Whatever else holds the slot, as another rules' state in a process that switched, keeps it; the part runs on
        // the state just made.
        if (!Replaceable(seen))
            return state;
        object? found = Interlocked.CompareExchange(ref IlEvmFrameRunner.CodeSlot(codeInfo), state, seen);
        return ReferenceEquals(found, seen) ? state
            : found is IlSimpleState other && other.Generation == state.Generation && ReferenceEquals(other.Bodies, state.Bodies) ? other : state;
    }

    private static bool Replaceable(object? seen) => seen is null or IlSimpleMark or IlSimpleState or IlQuiet;

    /// <summary>The compiled set changed: every state and mark resolves again on its next part.</summary>
    public static void Changed()
    {
        Volatile.Write(ref Nothing, new IlSimpleMark("nothing"));
        Volatile.Write(ref NothingBlock, new IlSimpleMark("nothing: block"));
        Interlocked.Increment(ref Generation);
    }

    /// <summary>Publishes the startup list's contracts, found by their keccak and, for code no cache stamped, by their bytes.</summary>
    /// <remarks>
    /// Adds to what was published, as the retry of the entries the code database lacked does. A contract promoted for
    /// the same body set meanwhile, under the code's keccak or its bytes, is evicted: the startup one serves it now.
    /// </remarks>
    public static void PublishStartup(IReadOnlyList<(IlEvmPrecompileList.Entry Entry, IlEvm.IlContract Contract)> contracts)
    {
        if (contracts.Count == 0)
            return;
        lock (_lock)
        {
            foreach ((IlEvmPrecompileList.Entry entry, IlEvm.IlContract contract) in contracts)
            {
                int length = contract.Code.Length;
                IlSimpleProfile created = new(entry.Hash, length);
                IlSimpleProfile profile = _byHash.GetOrAdd(entry.Hash, created);
                if (ReferenceEquals(profile, created))
                    Interlocked.Increment(ref _profiles);
                if (profile.Startup is not null)
                    continue;
                profile.Startup = contract;
                if (profile.Promoted is { } promoted && ReferenceEquals(promoted.Bodies, contract.Bodies))
                    Evict(profile);
                profile.Bytes = contract.Code;
                // A startup contract also serves override code of the same bytes, and replaces a content profile of them.
                if (!profile.InContentIndex)
                    AddToIndex(profile, evicted: null);
                Interlocked.Increment(ref _startup);
                Interlocked.Add(ref _startupBytes, contract.MemoryBytes);
            }
        }

        Changed();
    }

    /// <summary>The IL and tables of the startup contracts.</summary>
    public static long StartupBytes => Interlocked.Read(ref _startupBytes);

    /// <summary>Starts the promotion rounds on their own thread, unless <see cref="Promote"/> is off; the code database is where a stamped code collected from memory is read from.</summary>
    public static void Start(ILogger logger, IDb? codeDb)
    {
        _logger = logger;
        _codeDb = codeDb;
        _stopping = false;
        _lastRoundTicks = Stopwatch.GetTimestamp();
        _lastMissingRetry = Environment.TickCount64;
        if (_thread is not null || ManualForTests)
            return;
        Thread thread = new(Loop) { IsBackground = true, Name = "EVM IL promoter", Priority = ThreadPriority.Lowest };
        _thread = thread;
        thread.Start();
    }

    /// <summary>Stops the rounds, at shutdown.</summary>
    public static void Stop()
    {
        _stopping = true;
        _wake.Set();
    }

    private static void Loop()
    {
        LowerPriority();
        while (!_stopping)
        {
            _wake.WaitOne(Math.Max(1, PromoteMs));
            if (_stopping)
                break;
            try
            {
                long now = Stopwatch.GetTimestamp();
                double seconds = (now - _lastRoundTicks) / (double)Stopwatch.Frequency;
                _lastRoundTicks = now;
                if (Promote)
                    Round(seconds);
                if (Environment.TickCount64 - _lastMissingRetry >= RetryMissingMs)
                {
                    _lastMissingRetry = Environment.TickCount64;
                    int found = IlShippedBacklog.RetryMissing();
                    if (found > 0 && _logger.IsInfo)
                        _logger.Info($"IL-EVM: {found} startup list entries compiled once the code database held their code");
                }
            }
            catch (Exception exception)
            {
                // A round is an optional speed-up: whatever goes wrong leaves what was compiled, and the next round tries again.
                if (_logger.IsWarn)
                    _logger.Warn($"IL-EVM promotion round failed: {exception}");
            }
        }
    }

    /// <summary>
    /// The calling thread at the lowest priority it may take: the lowest .NET priority, which Windows honors; Linux ignores
    /// it for unprivileged threads, and the idle class, which it allows, is taken only under <see cref="IlEvmSettings.CompileIdle"/>.
    /// </summary>
    /// <remarks>
    /// A thread created by an idle-class thread inherits the class, and the runtime starts its tier-up worker on whichever
    /// thread first needs it, so an idle-class compiler could leave the whole node's tier-up to idle time while it is busy.
    /// </remarks>
    private static void LowerPriority()
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
        }
        catch (Exception)
        {
            // A platform that refuses keeps the default.
        }

        if (IlEvmSettings.CompileIdle && OperatingSystem.IsLinux())
            IlEvm.IlCompileQueue.SetIdleScheduling();
    }

    /// <summary>A round's candidate: its profile, rate and the code and body set it compiles for.</summary>
    private sealed class Candidate(IlSimpleProfile profile, double rate, byte[] code, OpcodeBodySet bodies)
    {
        public readonly IlSimpleProfile Profile = profile;
        public readonly double Rate = rate;
        public readonly byte[] Code = code;
        public readonly OpcodeBodySet Bodies = bodies;
        public IlEvm.IlContract? Contract;
        public string? Reason;
    }

    /// <summary>
    /// One promotion round over the counts of the last <paramref name="seconds"/>: folds every profile's counts, compiles the
    /// codes past <see cref="PromoteOps"/> that the budget could take, busiest first, and adds them; prunes idle profiles
    /// near the caps; logs a line when it promoted any.
    /// </summary>
    /// <returns>How many contracts the round promoted.</returns>
    internal static int Round(double seconds)
    {
        seconds = Math.Max(seconds, 1e-3);
        long round = Interlocked.Increment(ref _round);
        Interlocked.Increment(ref _rounds);
        List<(IlSimpleProfile Profile, double Rate)> hot = [];
        int idle = 0;
        foreach (IlSimpleProfile profile in AllProfiles())
        {
            (long parts, long ops, long compiledParts) = profile.Take();
            profile.Demand = profile.Demand / 2 + (double)parts * SampleEvery;
            profile.Usage = profile.Usage / 2 + (double)compiledParts * SampleEvery;
            profile.IdleRounds = parts + compiledParts == 0 ? profile.IdleRounds + 1 : 0;
            if (profile.IdleRounds >= IdleToPrune)
                idle++;
            // A code compiled for the body set it was last counted on is done; a contract for an older fork's is not.
            if (ops == 0 || profile.Declined || round < profile.RetryRound || profile.Bodies is not { } bodies ||
                profile.RunFor(bodies, block: false) is not null)
                continue;
            double rate = ops * (double)SampleEvery / seconds;
            if (rate >= PromoteOps)
                hot.Add((profile, rate));
        }

        List<Candidate> candidates = [];
        List<IlSimpleProfile> evicted = [];
        HashSet<IlSimpleProfile> taken = [];
        bool changed = false;
        foreach ((IlSimpleProfile profile, double rate) in hot.OrderByDescending(static h => h.Rate))
        {
            if (candidates.Count >= MaxPerRound)
                break;
            OpcodeBodySet bodies = profile.Bodies!;
            // Code no live code info or database has now, as one collected with its code infos where the database is
            // not at hand, is looked for again at a later round; a code info resolved meanwhile serves.
            if (CodeOf(profile) is not { Length: > 0 } code)
                continue;

            IlSimpleProfile target = profile;
            if (profile.ByContent)
            {
                // Override code whose bytes the code database also serves, found here by their keccak, off the frame path:
                // the stamped profile compiles and holds the code, and the content one is retired once it does.
                if (_byHash.TryGetValue(ValueKeccak.Compute(code), out IlSimpleProfile? twin) && !twin.Declined)
                {
                    if (twin.RunFor(bodies, block: false) is not null)
                    {
                        lock (_lock)
                            Remove([profile], evicted);
                        changed = true;
                        continue;
                    }

                    twin.Demand += profile.Demand;
                    target = twin;
                }
            }
            else if (FindByContent(code) is { } twin && !ReferenceEquals(twin, profile) && twin.RunFor(bodies, block: false) is not null)
            {
                // A contract compiled from the same bytes serves the code: its states find it at the next generation.
                changed = true;
                continue;
            }

            if (round < target.RetryRound || !taken.Add(target))
                continue;
            // Compiled only if the budget could take it, by the size it had when last turned away.
            if (!CouldAdmit(target, Math.Max(1, target.RejectedBytes)))
            {
                target.RetryRound = round + BudgetRetryRounds;
                Interlocked.Increment(ref _rejected);
                continue;
            }

            candidates.Add(new Candidate(target, rate, code, bodies));
        }

        Compile(candidates);
        List<IlSimpleProfile> promoted = [];
        bool pruned = false;
        lock (_lock)
        {
            foreach (Candidate candidate in candidates)
            {
                if (candidate.Contract is not { } contract)
                {
                    candidate.Profile.Declined = true;
                    Interlocked.Increment(ref _declined);
                    IlEvmSettings.CountFailed(candidate.Reason);
                    continue;
                }

                if (!Admit(candidate.Profile, contract, evicted))
                {
                    candidate.Profile.RetryRound = round + BudgetRetryRounds;
                    Interlocked.Increment(ref _rejected);
                    continue;
                }

                promoted.Add(candidate.Profile);
            }

            if (idle > 0 && (_contentFull || Volatile.Read(ref _profiles) > MaxProfiles * 3 / 4))
            {
                _contentFull = false;
                pruned = Prune();
            }
        }

        if (changed || promoted.Count > 0 || evicted.Count > 0 || pruned)
            Changed();
        if (promoted.Count > 0)
        {
            Interlocked.Increment(ref _promotionRounds);
            if (_logger.IsInfo)
            {
                string evictions = evicted.Count > 0 ? $", evicted {evicted.Count}: {string.Join(" ", evicted.Select(static p => p.ShortName))}" : "";
                _logger.Info(string.Create(CultureInfo.InvariantCulture,
                    $"IL-EVM promoted {promoted.Count}: {string.Join(" ", promoted.Select(static p => p.ShortName))}; budget {UsedBytes / (double)(1 << 20):F1} of {BudgetBytes >> 20} MiB, {PromotedCount} held{evictions}"));
            }
        }

        return promoted.Count;
    }

    /// <summary>Compiles the candidates on up to <see cref="CompileThreads"/> threads, the calling one among them, each whole.</summary>
    private static void Compile(List<Candidate> candidates)
    {
        if (candidates.Count == 0)
            return;
        int next = -1;
        void Work()
        {
            int index;
            while ((index = Interlocked.Increment(ref next)) < candidates.Count)
            {
                Candidate candidate = candidates[index];
                Interlocked.Increment(ref _compiles);
                try
                {
                    candidate.Contract = IlEvm.Precompile(candidate.Code, candidate.Bodies, out candidate.Reason);
                }
                catch (Exception exception)
                {
                    candidate.Reason = exception.Message;
                }
            }
        }

        int helpers = Math.Min(Math.Max(1, CompileThreads), candidates.Count) - 1;
        Thread[] threads = new Thread[helpers];
        for (int i = 0; i < helpers; i++)
        {
            threads[i] = new Thread(() =>
            {
                LowerPriority();
                Work();
            }) { IsBackground = true, Name = $"EVM IL promoter {i + 1}", Priority = ThreadPriority.Lowest };
            threads[i].Start();
        }

        Work();
        foreach (Thread thread in threads)
            thread.Join();
    }

    /// <summary>The promoted contracts <paramref name="profile"/> may evict, least used first: those other than its own used less than it is in demand.</summary>
    private static IEnumerable<IlSimpleProfile> Evictable(IlSimpleProfile profile) =>
        _promoted.Where(p => !ReferenceEquals(p, profile) && p.Usage < profile.Demand).OrderBy(static p => p.Usage);

    /// <summary>Whether a contract of <paramref name="bytes"/> for <paramref name="profile"/> could be admitted, before it is compiled: the budget's room, its own older contract's and what it may evict.</summary>
    private static bool CouldAdmit(IlSimpleProfile profile, long bytes)
    {
        lock (_lock)
        {
            if (bytes > BudgetBytes)
                return false;
            long room = BudgetBytes - _usedBytes + (profile.Promoted?.MemoryBytes ?? 0);
            if (room >= bytes)
                return true;
            foreach (IlSimpleProfile held in Evictable(profile))
            {
                room += held.Promoted?.MemoryBytes ?? 0;
                if (room >= bytes)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Adds a promoted contract within the budget, under the lock: the profile's own contract for an older fork is
    /// replaced, and the least used promoted contracts used less than the newcomer is in demand are evicted first, as
    /// many as it needs. False, with nothing evicted and the contract dropped, when it would still not fit the budget or
    /// <see cref="IlEvmSettings.MaxLiveIlBytes"/>.
    /// </summary>
    private static bool Admit(IlSimpleProfile profile, IlEvm.IlContract contract, List<IlSimpleProfile> evicted)
    {
        long bytes = contract.MemoryBytes;
        IlEvm.IlContract? replaced = profile.Promoted;
        long replacedBytes = replaced?.MemoryBytes ?? 0;
        long room = BudgetBytes - _usedBytes + replacedBytes;
        List<IlSimpleProfile> victims = [];
        long freed = 0;
        if (room < bytes)
        {
            foreach (IlSimpleProfile held in Evictable(profile))
            {
                victims.Add(held);
                freed += held.Promoted?.MemoryBytes ?? 0;
                if (room + freed >= bytes)
                    break;
            }
        }

        // MaxLiveIlBytes stays the hard cap on what run-time compiles hold, checked before anything is evicted.
        if (bytes > BudgetBytes || room + freed < bytes || IlEvmSettings.LiveIlBytes - freed - replacedBytes + bytes > IlEvmSettings.MaxLiveIlBytes)
        {
            profile.RejectedBytes = bytes;
            return false;
        }

        foreach (IlSimpleProfile victim in victims)
        {
            Evict(victim);
            evicted.Add(victim);
        }

        contract.MarkLive();
        profile.Promoted = contract;
        if (replaced is not null)
        {
            // Another fork's contract of the same code: its frames hand back meanwhile, as an evicted one's do.
            _usedBytes -= replacedBytes;
            replaced.Release(dropTables: true);
        }
        else
        {
            _promoted.Add(profile);
        }

        _usedBytes += bytes;
        profile.Usage = Math.Max(profile.Usage, profile.Demand);
        profile.RejectedBytes = 0;
        Interlocked.Increment(ref _promotedTotal);
        // Override code of a promoted stamped contract's bytes finds it too.
        if (!profile.InContentIndex)
        {
            profile.Bytes = contract.Code;
            AddToIndex(profile, evicted);
        }

        return true;
    }

    /// <summary>Takes a promoted contract out of the compiled set and releases its code and tables, under the lock; its frames hand back meanwhile, and the code is counted again.</summary>
    private static void Evict(IlSimpleProfile profile)
    {
        if (profile.Promoted is not { } contract)
            return;
        profile.Promoted = null;
        _promoted.Remove(profile);
        _usedBytes -= contract.MemoryBytes;
        profile.Usage = 0;
        // The tables too: a code info that never runs again may keep its state, and the contract with it, indefinitely.
        contract.Release(dropTables: true);
        Interlocked.Increment(ref _evicted);
    }

    /// <summary>
    /// Forgets the profiles idle for <see cref="IdleToPrune"/> rounds that hold nothing, under the lock: content ones are
    /// retired from the index, their kept bytes given back; stamped ones leave the keccak table. Their states resolve
    /// again with the next generation.
    /// </summary>
    /// <returns>Whether any was forgotten.</returns>
    private static bool Prune()
    {
        HashSet<IlSimpleProfile> unindexed = [];
        foreach (IlSimpleProfile profile in _content)
        {
            if (profile.IdleRounds >= IdleToPrune && !profile.HoldsAny)
                unindexed.Add(profile);
        }

        int removed = unindexed.Count;
        foreach (KeyValuePair<ValueHash256, IlSimpleProfile> entry in _byHash)
        {
            IlSimpleProfile profile = entry.Value;
            if (profile.IdleRounds >= IdleToPrune && !profile.HoldsAny && _byHash.TryRemove(entry))
            {
                removed++;
                Interlocked.Decrement(ref _profiles);
                if (profile.InContentIndex)
                    unindexed.Add(profile);
            }
        }

        if (unindexed.Count > 0)
            Remove(unindexed, evicted: null);
        return removed > 0;
    }

    private static IEnumerable<IlSimpleProfile> AllProfiles()
    {
        foreach (KeyValuePair<ValueHash256, IlSimpleProfile> entry in _byHash)
            yield return entry.Value;
        foreach (IlSimpleProfile profile in Volatile.Read(ref _content))
            yield return profile;
    }

    /// <summary>The code a live compile compiles: the bytes a profile keeps, as a content or startup one does, else a live code info's, else the code database's.</summary>
    private static byte[]? CodeOf(IlSimpleProfile profile)
    {
        if (!profile.Bytes.IsEmpty || profile.ByContent)
            return profile.Bytes.IsEmpty ? null : profile.Bytes.ToArray();
        if (profile.Source is { } source && source.TryGetTarget(out CodeInfo? codeInfo) && codeInfo.CodeSpan.Length > 0)
            return codeInfo.CodeSpan.ToArray();
        return _codeDb?.Get(profile.Hash.Bytes);
    }

    /// <summary>The exit line's part: the compiled set, what promotion did, and the parts each runner ran compiled and interpreted.</summary>
    public static string Describe()
    {
        (long qc, long qi, long bc, long bi) = IlSimpleThread.Totals();
        (int promoted, int evicted, int rejected, int declined) = Totals;
        (int rounds, int promoting) = Rounds;
        return string.Create(CultureInfo.InvariantCulture,
            $"compiled set {StartupCount + PromotedCount} ({StartupCount} startup, {StartupBytes >> 20} MiB; {PromotedCount} promoted, {UsedBytes / (double)(1 << 20):F1} of {BudgetBytes >> 20} MiB); " +
            $"promoted {promoted} in {promoting} of {rounds} rounds, evicted {evicted}, turned away by the budget {rejected}, declined {declined}; " +
            $"parts compiled/interpreted: queries {qc}/{qi}, blocks {bc}/{bi}; {ProfileCount} profiles, {Resolved} states resolved");
    }

    /// <summary>Forgets everything, for tests: profiles, the compiled set and the counts; code infos resolved before resolve again.</summary>
    internal static void ResetForTests()
    {
        lock (_lock)
        {
            foreach (IlSimpleProfile profile in _promoted)
                profile.Promoted?.Release(dropTables: true);
            _promoted.Clear();
            _byHash.Clear();
            Volatile.Write(ref _byLength, []);
            Volatile.Write(ref _content, []);
            _profiles = _startup = _promotedTotal = _evicted = _rejected = _declined = _rounds = _promotionRounds = _compiles = 0;
            _retainedBytes = _usedBytes = _startupBytes = _resolved = _round = 0;
            _contentFull = false;
        }

        Changed();
    }

    /// <summary>Counts a sampled interpreted part of a code not compiled into its profile, with the body set a live compile is for; its opcodes follow in <see cref="CountOps"/>.</summary>
    /// <remarks>Plain writes into the thread's stripe: a lost count only moves a rate, and a body set is written only when it changes.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Sample(IlSimpleProfile profile, OpcodeBodySet bodies, IlSimpleThread thread)
    {
        profile.Counts.AddPart(thread.Stripe);
        if (!ReferenceEquals(profile.Bodies, bodies))
            profile.Bodies = bodies;
    }

    /// <summary>Whether the part is drawn for the sample: one in <see cref="SampleEvery"/>, at random per thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Draw(IlSimpleThread thread) => (thread.Next() & SampleMask) == 0;

    /// <summary>The interpreter ran <paramref name="opCodes"/> opcodes of a sampled part of <paramref name="codeInfo"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CountOps(CodeInfo codeInfo, int opCodes)
    {
        if (IlEvmFrameRunner.CodeSlot(codeInfo) is IlSimpleState { Count: { } profile })
            profile.Counts.AddOps(IlSimpleThread.Current.Stripe, opCodes);
    }
}
