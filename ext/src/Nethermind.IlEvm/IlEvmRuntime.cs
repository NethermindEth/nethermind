// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

/// <summary>Where a compiled contract came from: the list shipped with the plugin, or live profiling of one source.</summary>
internal enum IlOrigin : byte
{
    /// <summary>The shipped list, compiled at startup or, past its deadline, between blocks.</summary>
    Shipped,
    LiveRpc,
    LiveBlocks,
}

/// <summary>A code's identity among the profiles: the keccak of code a cache stamped, else a fast hash of its content; with its length either way.</summary>
/// <remarks>A content hash collision only shares a profile, never code: a contract is attached to such code only once their bytes compare equal.</remarks>
internal readonly record struct IlProfileKey(ValueHash256 Keccak, long Content, int Length)
{
    public static IlProfileKey ByHash(in ValueHash256 keccak, int length) => new(keccak, 0, length);

    public static IlProfileKey ByContent(long content, int length) => new(default, content, length);

    public bool IsByContent => Keccak == default;
}

/// <summary>What the runtime keeps of one code content, whichever code infos carry it: its compiled contracts and the list's word on it.</summary>
/// <remarks>A profile holds no code info; only code no cache stamped keeps its bytes, in <see cref="Retained"/>.</remarks>
internal sealed partial class IlCodeProfile(IlProfileKey key, int size)
{
    private static int _lastId;

    /// <summary>A small number unique to the profile, for what is salted or hashed per contract.</summary>
    public readonly int Id = Interlocked.Increment(ref _lastId);

    public readonly IlProfileKey Key = key;

    public readonly int Size = size;

    /// <summary>The list's <c>minv=</c>: the log2 of the visit length from which block processing's runner admits the contract, or 0 for the runner's default.</summary>
    public byte BlockMinVisitLog2;

    /// <summary>The list's <c>use=</c>: which kinds of thread may run the shipped contract, as the bits of <see cref="IlRuntime.RunAll"/>; all of them for an entry without it.</summary>
    public byte UseMask = IlRuntime.RunAll;

    /// <summary>Whether the list gave <c>qminv=</c>: the queries' gate then admits visits of keys not learned yet from their first part, not renting them.</summary>
    public bool QuerySeeded;

    /// <summary>The list's <c>qminv=</c>: the log2 of the visit length the queries' runner admits from a visit's first part; 0 for every visit.</summary>
    public byte QueryMinVisitLog2;

    /// <summary>The list's <c>score=</c>: what compiling the contract saved, in nanoseconds per block; null for an entry without it.</summary>
    public double? ListScore;

    /// <summary>The list's <c>L=</c>: the shape of the code's visits; null for an entry without it.</summary>
    public IlEvmCodeShape? Shape;

    /// <summary>The body set of the latest frame that resolved the code: the one a live compile is for.</summary>
    public OpcodeBodySet? LastBodies;

    /// <summary>The bytes of code no cache stamped, kept for a live compile, which has no code database to read them from; empty past <see cref="IlRuntime.MaxRetainedBytes"/>.</summary>
    public ReadOnlyMemory<byte> Retained;

    /// <summary>Set while, or once, <see cref="Retained"/> is taken, so that one thread pays for it.</summary>
    internal int Retaining;

    /// <summary>The contract the list shipped, compiled for the body set the node started on.</summary>
    public volatile IlEvm.IlContract? Shipped;

    /// <summary>The shipped contract compiled ahead for the next fork's body set, which a fork miss attaches.</summary>
    public volatile IlEvm.IlContract? ShippedNext;

    public volatile IlEvm.IlContract? Live;
}

/// <summary>A code slot's mark that a runner has nothing compiled for the code: the runner declines its frames without reading anything the slot points at.</summary>
/// <remarks>The hot path only compares a mark; a runner that meets an old one reads it, to see whether only the other runner's growth made it old.</remarks>
internal sealed class IlQuiet(string name, bool block, bool query, int blockGeneration, int queryGeneration)
{
    public readonly bool Block = block;
    public readonly bool Query = query;
    public readonly int BlockGeneration = blockGeneration;
    public readonly int QueryGeneration = queryGeneration;

    public bool Holds(bool block) => block
        ? Block && BlockGeneration == IlRuntime.BlockGeneration
        : Query && QueryGeneration == IlRuntime.QueryGeneration;

    public override string ToString() => name;
}

/// <summary>
/// The Adaptive rules' lookup: which compiled contract, if any, each runner runs a code on, resolved once per code info
/// and runtime version and kept in the code's slot.
/// </summary>
/// <remarks>A slot holds an <see cref="IlEvm.IlCodeState"/> or an <see cref="IlQuiet"/> mark; one state serves both runners, as a code cache hands both the same code infos.</remarks>
internal static partial class IlRuntime
{
    /// <summary>The <see cref="IlEvm.IlContract.RunMask"/> bit of block processing's own thread.</summary>
    public const byte RunMain = 1;

    /// <summary>The bit of the other threads block processing's runner serves: prewarming, production, the mempool's session.</summary>
    public const byte RunWorkers = 2;

    public const byte RunQuery = 4;

    public const byte RunAll = RunMain | RunWorkers | RunQuery;

    /// <summary>Set in a run mask for a second after block processing's check takes its runner off: the parts it declines then are looked at for a visit under way.</summary>
    public const byte CheckOffBlock = 0x10;

    /// <summary>As <see cref="CheckOffBlock"/>, for the read-only queries' runner's check.</summary>
    public const byte CheckOffQuery = 0x20;

    /// <summary>Moves on every change to what may run; a state resolved at an older one resolves again on its next part.</summary>
    public static int Version = 1;

    public static IlQuiet QuietBlock = new("quiet: block", block: true, query: false, 0, 0);

    public static IlQuiet QuietQuery = new("quiet: query", block: false, query: true, 0, 0);

    /// <summary>The mark of code neither runner has anything for, so code both run is not marked back and forth.</summary>
    public static IlQuiet QuietAll = new("quiet: both", block: true, query: true, 0, 0);

    /// <summary>Whether the read-only queries' runner counts for live profiling: Adaptive with <see cref="IlEvmPrefer.Rpc"/>.</summary>
    /// <remarks>
    /// A counting runner passes marks in the transactions it samples, so code they meet gets a state and a profile; block
    /// processing then reads that shared state's first line rather than a mark, until a new version marks the code again.
    /// </remarks>
    public static bool QueryCounting;

    /// <summary>Whether block processing's runner counts: with <see cref="IlEvmPrefer.Blocks"/>, set by the scheduler for a branch of at most two blocks while not syncing.</summary>
    public static volatile bool BlocksCounting;

    /// <summary>Live profiling's epoch for block processing: the counted blocks, moved by the scheduler at each one's start.</summary>
    public static int BlockEpoch;

    public static int RpcEpoch => ManualRpcEpoch >= 0 ? ManualRpcEpoch : (int)((Environment.TickCount64 - IlEvmSettings.ProcessStartedAt) / 1000);

    internal static int ManualRpcEpoch = -1;

    /// <summary>
    /// Whether block processing is in a branch, as the scheduler sees it. Inside one, its checks sample every thread it
    /// serves; outside, only its own thread's passes, so the mempool's session and block production never time a contract.
    /// </summary>
    public static volatile bool InBranch;

    /// <summary>The kinds of block-processing thread <see cref="IIlEvmConfig.BlockThreads"/> lets run compiled code, as <see cref="RunMain"/> and <see cref="RunWorkers"/> bits.</summary>
    public static int BlockThreadsMask = RunMain | RunWorkers;

    /// <summary>The most profiles kept; past it, code a counting runner meets gets none, while a list entry always gets one.</summary>
    public static int MaxProfiles { get; set; } = 32_768;

    /// <summary>The most bytes of code no cache stamped the profiles keep for live compiles.</summary>
    public static long MaxRetainedBytes { get; set; } = 16L << 20;

    public static readonly ConcurrentDictionary<IlProfileKey, IlCodeProfile> Profiles = new();

    /// <summary>The list file's <c># calib</c> lines, without the prefix, which the model and the block runner's gate read.</summary>
    public static IReadOnlyList<string> Calibration { get; set; } = [];

    private static readonly object _publishing = new();

    /// <summary>The shipped profiles by code length, to find code no cache stamped by its bytes; copied on write.</summary>
    private static Dictionary<int, IlCodeProfile[]>? _shippedByLength;

    private static int _blockGeneration;
    private static int _queryGeneration;
    private static int _profiles;
    private static long _retainedBytes;
    private static long _shippedBytes;
    private static long _statesResolved;
    private static long _statesRefreshed;
    private static long _statesAttached;
    private static long _contentHashes;
    private static long _lookups;

    /// <summary>How often what block processing's runner may run grew; the marks carry it.</summary>
    public static int BlockGeneration => Volatile.Read(ref _blockGeneration);

    public static int QueryGeneration => Volatile.Read(ref _queryGeneration);

    public static int ProfileCount => Volatile.Read(ref _profiles);

    public static long RetainedBytes => Interlocked.Read(ref _retainedBytes);

    public static long ShippedBytes => Interlocked.Read(ref _shippedBytes);

    /// <summary>Code states created for code a runner met, and states resolved again at a newer version.</summary>
    public static long StatesResolved => Interlocked.Read(ref _statesResolved);

    public static long StatesRefreshed => Interlocked.Read(ref _statesRefreshed);

    public static long StatesAttached => Interlocked.Read(ref _statesAttached);

    /// <summary>Content hashes taken of code no cache stamped, at most one per state.</summary>
    public static long ContentHashes => Interlocked.Read(ref _contentHashes);

    /// <summary>Lookups of a code among the profiles, at most one per resolution.</summary>
    public static long Lookups => Interlocked.Read(ref _lookups);

    internal static IReadOnlyDictionary<int, IlCodeProfile[]>? ShippedLengthIndex => Volatile.Read(ref _shippedByLength);

    /// <summary>Resolves a code info whose slot holds no state yet: <paramref name="seen"/>, nothing or another runner's or an old mark.</summary>
    /// <param name="counting">Whether the runner counts for live profiling, so profiles of code no cache stamped exist to be found by its content.</param>
    /// <param name="sampling">Whether the transaction is sampled: code with no profile gets one, and code with nothing to run a state for its counts.</param>
    /// <returns>The state, or null with the slot marked when the runner declines the code.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IlEvm.IlCodeState? Resolve(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies, object? seen, bool block, bool counting, bool sampling)
    {
        // The marks and the version first: a publish replaces them after it publishes, so neither is newer than what
        // the lookup below sees, and a slot marked or a state made on stale data resolves again.
        IlQuiet own = block ? Volatile.Read(ref QuietBlock) : Volatile.Read(ref QuietQuery);
        IlQuiet all = Volatile.Read(ref QuietAll);
        int version = Volatile.Read(ref Version);

        // Initcode is never in the code database, so no list ships it and nothing compiles it.
        if (vm.VmState.ExecutionType.IsAnyCreate())
            return Mark(codeInfo, seen, own);
        // Block processing's code comes stamped from the code database; anything else is left to the interpreter unhashed.
        if (block && codeInfo.CodeHash == default)
            return Mark(codeInfo, seen, own);
        // A mark made old only by the other runner's growth still holds here: the slot takes this runner's own mark, with
        // no lookup, and the other runner resolves the code when it meets it.
        if (!sampling && seen is IlQuiet old && old.Holds(block))
            return Mark(codeInfo, seen, own);

        long content = 0;
        bool hasContent = false;
        IlCodeProfile? profile = Lookup(codeInfo, counting, hashing: true, ref content, ref hasContent) ??
            (sampling ? Create(codeInfo, content, hasContent, block) : null);
        IlEvm.IlContract? blockRun = profile is null ? null : RunFor(profile, codeInfo, block: true, bodies);
        IlEvm.IlContract? queryRun = profile is null ? null : RunFor(profile, codeInfo, block: false, bodies);
        IlEvm.IlContract? run = block ? blockRun : queryRun;
        // Nothing to run or count (code with no profile counts nothing, as past the table's cap): marked, for both
        // runners when the other has nothing either.
        if (run is null && (!sampling || profile is null))
            return Mark(codeInfo, seen, (block ? queryRun : blockRun) is null ? all : own);

        IlEvm.IlCodeState state = new()
        {
            Profile = profile,
            ContentHash = content,
            HasContentHash = hasContent,
            BlockRun = blockRun,
            QueryRun = queryRun,
            Version = version,
        };
        if (profile is not null)
            profile.LastBodies = bodies;
        Interlocked.Increment(ref _statesResolved);
        if (run is not null && ReferenceEquals(run.Bodies, bodies))
            Attached(run);
        return Install(codeInfo, seen, state);
    }

    /// <summary>Resolves a state made at an older <see cref="Version"/> again, for both runners, and brings it to the current one.</summary>
    /// <returns>The state, or null when no runner has anything left to run or count and the slot takes the mark for both.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IlEvm.IlCodeState? Refresh(VirtualMachine<EthereumGasPolicy> vm, IlEvm.IlCodeState state, CodeInfo codeInfo, OpcodeBodySet bodies, bool block, bool counting, bool sampling)
    {
        // Only a process that switched rules has a state for initcode, which the Legacy rules count.
        if (vm.VmState.ExecutionType.IsAnyCreate())
            return null;

        // The mark and the version before the lookup, as for a new state.
        IlQuiet all = Volatile.Read(ref QuietAll);
        int version = Volatile.Read(ref Version);
        IlCodeProfile? profile;
        if (block && codeInfo.CodeHash == default && !Volatile.Read(ref state.HasContentHash))
        {
            // Block processing hashes nothing: code no cache stamped keeps what the state had.
            profile = state.Profile;
        }
        else
        {
            profile = Lookup(codeInfo, counting, hashing: !block, ref state.ContentHash, ref state.HasContentHash) ??
                (sampling ? Create(codeInfo, state.ContentHash, Volatile.Read(ref state.HasContentHash), block) : null);
        }

        IlEvm.IlContract? blockRun = profile is null ? null : RunFor(profile, codeInfo, block: true, bodies);
        IlEvm.IlContract? queryRun = profile is null ? null : RunFor(profile, codeInfo, block: false, bodies);
        Interlocked.Increment(ref _statesRefreshed);
        // Nothing to run or count. A counting runner's next sampled transaction resolves the code from the mark and makes
        // its profile then; a state kept with none would keep it from ever having one.
        if (blockRun is null && queryRun is null && (profile is null || (!QueryCounting && !BlocksCounting)) && state.Contract is null && state.Shipped is null)
        {
            // An empty state costs every part a read, the mark none; one the Legacy rules compiled for, after a switch, is kept.
            Interlocked.CompareExchange(ref IlEvmFrameRunner.CodeSlot(codeInfo), all, state);
            return null;
        }

        state.Profile = profile;
        state.BlockRun = blockRun;
        state.QueryRun = queryRun;
        // What may run changed: a body set that found nothing before may find something now.
        state.BlockMissed = state.QueryMissed = null;
        if (profile is not null)
            profile.LastBodies = bodies;
        // Last, so a part that reads the new version reads the runs it was resolved with.
        Volatile.Write(ref state.Version, version);
        IlEvm.IlContract? run = block ? blockRun : queryRun;
        if (run is not null && ReferenceEquals(run.Bodies, bodies))
            Attached(run);
        return state;
    }

    /// <summary>A part of a body set other than its runner's contract's: the profile's contract for that body set, which the state takes from now on, or null for the interpreter.</summary>
    /// <remarks>
    /// On a miss the state keeps its contract for its own body set and records the missed one, whose later parts go to the
    /// interpreter without coming here until the state resolves again. Only a contract compiled ahead for the next fork
    /// attaches here with no new version; any other publish for a body set parts already missed must move the version.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IlEvm.IlContract? ForkMiss(IlEvm.IlCodeState state, CodeInfo codeInfo, OpcodeBodySet bodies, bool block)
    {
        Interlocked.Increment(ref _forkMisses);
        // Read first: a contract compiled ahead and published after it is looked for again.
        int missEpoch = Volatile.Read(ref MissEpoch);
        IlCodeProfile? profile = state.Profile;
        if (profile is null || RunnableFor(profile, block, bodies) is not { } contract || !Matches(profile, contract, codeInfo))
        {
            state.MissEpoch = missEpoch;
            if (block)
                state.BlockMissed = bodies;
            else
                state.QueryMissed = bodies;
            if (profile is not null && !ReferenceEquals(profile.LastBodies, bodies))
                profile.LastBodies = bodies;
            return null;
        }

        if (block)
            state.BlockRun = contract;
        else
            state.QueryRun = contract;
        Attached(contract);
        return contract;
    }

    /// <summary>Moves when a contract compiled ahead for another fork is published: the body sets states found nothing for are looked for again, with no new version.</summary>
    public static int MissEpoch;

    private static long _forkMisses;

    /// <summary>Parts that missed their state's contract's body set and looked for another; once per state and body set.</summary>
    public static long ForkMisses => Interlocked.Read(ref _forkMisses);

    /// <summary>Publishes a contract compiled ahead for the next fork's body set: <see cref="ForkMiss"/> attaches it with no new version, even where parts already missed.</summary>
    public static void PublishNext(IlCodeProfile profile, IlEvm.IlContract contract)
    {
        contract.Profile = profile;
        profile.ShippedNext = contract;
        Interlocked.Increment(ref MissEpoch);
    }

    /// <summary>The contract a runner may run the profile's code on for <paramref name="bodies"/>; block processing never runs the queries' promotions.</summary>
    public static IlEvm.IlContract? RunnableFor(IlCodeProfile profile, bool block, OpcodeBodySet bodies)
    {
        if (block && IlEvmSettings.BlockDeclineAll)
            return null;
        IlEvm.IlContract? contract = profile.Shipped;
        if (contract is not null && ReferenceEquals(contract.Bodies, bodies))
            return contract;
        contract = profile.ShippedNext;
        if (contract is not null && ReferenceEquals(contract.Bodies, bodies))
            return contract;
        contract = profile.Live;
        return contract is not null && ReferenceEquals(contract.Bodies, bodies) && (!block || contract.Origin == IlOrigin.LiveBlocks) ? contract : null;
    }

    private static IlEvm.IlContract? AnyFor(IlCodeProfile profile, bool block)
    {
        if (block && IlEvmSettings.BlockDeclineAll)
            return null;
        if ((profile.Shipped ?? profile.ShippedNext) is { } shipped)
            return shipped;
        IlEvm.IlContract? live = profile.Live;
        return live is not null && (!block || live.Origin == IlOrigin.LiveBlocks) ? live : null;
    }

    /// <summary>What a state names for a runner: the contract for these bodies, else any, so other body sets find theirs through <see cref="ForkMiss"/>.</summary>
    private static IlEvm.IlContract? RunFor(IlCodeProfile profile, CodeInfo codeInfo, bool block, OpcodeBodySet bodies)
    {
        IlEvm.IlContract? contract = RunnableFor(profile, block, bodies) ?? AnyFor(profile, block);
        return contract is not null && Matches(profile, contract, codeInfo) ? contract : null;
    }

    private static bool Matches(IlCodeProfile profile, IlEvm.IlContract contract, CodeInfo codeInfo) =>
        !profile.Key.IsByContent || codeInfo.CodeSpan.SequenceEqual(contract.Code.Span);

    /// <summary>The code's profile: by the stamped keccak, else by content, a shipped one first.</summary>
    /// <param name="hashing">Whether the content of code no cache stamped may be hashed here; a hash already taken is used either way.</param>
    private static IlCodeProfile? Lookup(CodeInfo codeInfo, bool counting, bool hashing, ref long content, ref bool hasContent)
    {
        Interlocked.Increment(ref _lookups);
        int length = codeInfo.CodeSpan.Length;
        ValueHash256 hash = codeInfo.CodeHash;
        if (hash != default)
            return Profiles.TryGetValue(IlProfileKey.ByHash(in hash, length), out IlCodeProfile? stamped) ? stamped : null;

        // Unstamped code: the shipped contracts by content, then live profiling's content profiles, kept only where a runner counts.
        if ((hashing || Volatile.Read(ref hasContent)) && ShippedByContent(codeInfo) is { } shipped)
            return shipped;
        if (!Volatile.Read(ref hasContent))
        {
            if (!counting || !hashing)
                return null;
            HashContent(codeInfo, ref content, ref hasContent);
        }

        return Profiles.TryGetValue(IlProfileKey.ByContent(content, length), out IlCodeProfile? byContent) ? byContent : null;
    }

    /// <summary>
    /// The shipped profile whose code these unstamped bytes equal. Not by keccak or content hash: both read all the code,
    /// and a state override's code info is new on every call its code cache misses; a compare mostly stops early.
    /// </summary>
    internal static IlCodeProfile? ShippedByContent(CodeInfo codeInfo)
    {
        ReadOnlySpan<byte> code = codeInfo.CodeSpan;
        if (Volatile.Read(ref _shippedByLength) is not { } index || !index.TryGetValue(code.Length, out IlCodeProfile[]? candidates))
            return null;
        foreach (IlCodeProfile profile in candidates)
        {
            IlEvm.IlContract? contract = profile.Shipped ?? profile.ShippedNext;
            if (contract is not null && code.SequenceEqual(contract.Code.Span))
                return profile;
        }

        return null;
    }

    private static void HashContent(CodeInfo codeInfo, ref long content, ref bool hasContent)
    {
        if (Volatile.Read(ref hasContent))
            return;
        content = IlEvm.IlContentHash(codeInfo);
        Volatile.Write(ref hasContent, true);
        Interlocked.Increment(ref _contentHashes);
    }

    private static IlCodeProfile? Create(CodeInfo codeInfo, long content, bool hasContent, bool block)
    {
        int length = codeInfo.CodeSpan.Length;
        ValueHash256 hash = codeInfo.CodeHash;
        if ((hash == default && !hasContent) || Volatile.Read(ref _profiles) >= MaxProfiles)
            return null;
        IlProfileKey key = hash != default ? IlProfileKey.ByHash(in hash, length) : IlProfileKey.ByContent(content, length);
        IlCodeProfile profile = Add(key, length, IlLive.Epoch(block));
        if (key.IsByContent)
            Retain(profile, codeInfo.Code);
        return profile;
    }

    private static IlCodeProfile Add(IlProfileKey key, int length, int epoch = 0)
    {
        IlCodeProfile created = new(key, length) { CreatedEpoch = epoch };
        IlCodeProfile profile = Profiles.GetOrAdd(key, created);
        if (ReferenceEquals(profile, created))
            Interlocked.Increment(ref _profiles);
        return profile;
    }

    /// <summary>The table forgets a profile and its retained bytes go back; states that hold it keep counting into it until they resolve again.</summary>
    internal static bool Evict(IlCodeProfile profile)
    {
        if (!Profiles.TryRemove(new KeyValuePair<IlProfileKey, IlCodeProfile>(profile.Key, profile)))
            return false;
        Interlocked.Decrement(ref _profiles);
        // No later state retains for it; what it retained, if anything, goes back.
        if (Interlocked.Exchange(ref profile.Retaining, 1) == 1 && !profile.Retained.IsEmpty)
        {
            Interlocked.Add(ref _retainedBytes, -profile.Retained.Length);
            profile.Retained = default;
        }

        return true;
    }

    private static void Retain(IlCodeProfile profile, ReadOnlyMemory<byte> code)
    {
        if (Volatile.Read(ref profile.Retaining) != 0 || Interlocked.CompareExchange(ref profile.Retaining, 1, 0) != 0)
            return;
        if (Interlocked.Add(ref _retainedBytes, code.Length) <= MaxRetainedBytes)
        {
            profile.Retained = code;
            return;
        }

        // Past the budget: the profile still counts, with nothing to compile live; a later state of the code tries again.
        Interlocked.Add(ref _retainedBytes, -code.Length);
        Volatile.Write(ref profile.Retaining, 0);
    }

    private static IlEvm.IlCodeState? Mark(CodeInfo codeInfo, object? seen, IlQuiet mark)
    {
        // Whatever else holds the slot, as a profiling run's counts, is not the runtime's to replace.
        if (!ReferenceEquals(seen, mark) && seen is null or IlQuiet)
            Interlocked.CompareExchange(ref IlEvmFrameRunner.CodeSlot(codeInfo), mark, seen);
        return null;
    }

    /// <summary>Installs <paramref name="state"/> in the slot over <paramref name="seen"/>; one that lost to another thread's state takes that one.</summary>
    private static IlEvm.IlCodeState Install(CodeInfo codeInfo, object? seen, IlEvm.IlCodeState state)
    {
        // A slot holding another kind of object keeps it, and the part runs on the state just made.
        if (seen is not (null or IlQuiet))
            return state;
        object? found = Interlocked.CompareExchange(ref IlEvmFrameRunner.CodeSlot(codeInfo), state, seen);
        return ReferenceEquals(found, seen) ? state : found as IlEvm.IlCodeState ?? state;
    }

    /// <summary>A state took <paramref name="contract"/> for a frame: the check's delay counts from its first frame.</summary>
    private static void Attached(IlEvm.IlContract contract)
    {
        Interlocked.Increment(ref _statesAttached);
        if (contract.AttachedAt == 0)
            contract.AttachedAt = Environment.TickCount64;
        IlEvmSettings.SawFrame();
    }

    /// <summary>Publishes contracts compiled for list entries, adding to what was published; every runner then resolves its marks and states again.</summary>
    public static void PublishShipped(IReadOnlyList<(IlEvmPrecompileList.Entry Entry, IlEvm.IlContract Contract)> shipped)
    {
        if (shipped.Count == 0)
            return;
        lock (_publishing)
        {
            Dictionary<int, IlCodeProfile[]> index = Volatile.Read(ref _shippedByLength) is { } published ? new(published) : [];
            foreach ((IlEvmPrecompileList.Entry entry, IlEvm.IlContract contract) in shipped)
            {
                int length = contract.Code.Length;
                IlCodeProfile profile = Add(IlProfileKey.ByHash(entry.Hash, length), length);
                profile.UseMask = entry.Use is { } use ? (byte)(((byte)use & (RunMain | RunWorkers)) | RunQuery) : RunAll;
                profile.BlockMinVisitLog2 = entry.MinVisitLog2 is > 0 and < 64 ? (byte)entry.MinVisitLog2.Value : (byte)0;
                profile.QuerySeeded = entry.QueryMinVisitLog2 is not null;
                profile.QueryMinVisitLog2 = (byte)Math.Clamp(entry.QueryMinVisitLog2 ?? 0, 0, 30);
                profile.ListScore = entry.Score;
                profile.Shape = entry.Shape;
                contract.Profile = profile;
                contract.Origin = IlOrigin.Shipped;
                contract.RunMask = (byte)((profile.UseMask & BlockThreadsMask) | RunQuery);
                IlCheck.Arm(contract);
                profile.Shipped = contract;
                // Each length's candidates once each, the list's first first.
                IlCodeProfile[] candidates = index.TryGetValue(length, out IlCodeProfile[]? known) ? known : [];
                if (Array.IndexOf(candidates, profile) < 0)
                    index[length] = [.. candidates, profile];
                Interlocked.Add(ref _shippedBytes, contract.MemoryBytes);
            }

            Volatile.Write(ref _shippedByLength, index);
            IlEvm.AddPrecompiled(shipped.Select(static s => (s.Entry.Hash, s.Contract)));
        }

        Grew(block: true, query: true);
    }

    /// <summary>What the runners may run grew: the marks that cover those that grew are replaced, and every state and old mark resolves again.</summary>
    /// <remarks>Called once what grew is published, so a runner that reads a new mark or generation finds it.</remarks>
    public static void Grew(bool block, bool query)
    {
        int blockGeneration = block ? Interlocked.Increment(ref _blockGeneration) : Volatile.Read(ref _blockGeneration);
        int queryGeneration = query ? Interlocked.Increment(ref _queryGeneration) : Volatile.Read(ref _queryGeneration);
        if (block)
            Volatile.Write(ref QuietBlock, new IlQuiet("quiet: block", block: true, query: false, blockGeneration, queryGeneration));
        if (query)
            Volatile.Write(ref QuietQuery, new IlQuiet("quiet: query", block: false, query: true, blockGeneration, queryGeneration));
        if (block || query)
            Volatile.Write(ref QuietAll, new IlQuiet("quiet: both", block: true, query: true, blockGeneration, queryGeneration));
        Interlocked.Increment(ref Version);
    }

    /// <summary>What may run shrank, as when a contract is released: every state resolves again, and marked code stays marked.</summary>
    public static void Shrank() => Interlocked.Increment(ref Version);

    internal static void ResetForTests()
    {
        lock (_publishing)
        {
            Profiles.Clear();
            Volatile.Write(ref _profiles, 0);
            Interlocked.Exchange(ref _retainedBytes, 0);
            Interlocked.Exchange(ref _shippedBytes, 0);
            Volatile.Write(ref _shippedByLength, null);
            Calibration = [];
            IlGate.Calibrate(Calibration);
            // The scheduler first: detached, it routes no release and restores nothing, and the checks' hooks go back.
            IlScheduler.ResetForTests();
            IlCheck.ResetForTests();
            IlLive.ResetForTests();
            BlockEpoch = 0;
        }

        Grew(block: true, query: true);
    }
}
