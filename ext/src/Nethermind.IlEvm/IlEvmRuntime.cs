// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

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
    /// <summary>Promoted by live profiling of the read-only queries, <see cref="IlEvmPrefer.Rpc"/>.</summary>
    LiveRpc,
    /// <summary>Promoted by live profiling of block processing, <see cref="IlEvmPrefer.Blocks"/>.</summary>
    LiveBlocks,
}

/// <summary>A code's identity among the profiles: the keccak of code a cache stamped, else a fast hash of its content; with its length either way.</summary>
/// <remarks>
/// Code no cache stamped, as from a state override, is never keccak-hashed on a frame's path. Its content hash is
/// cheap, and a collision only shares a profile, never code: a contract is attached to such code only once their bytes
/// compare equal.
/// </remarks>
/// <param name="Keccak">The stamped keccak, or default for code keyed by its content.</param>
/// <param name="Content">The content hash, as <see cref="IlEvm.IlContentHash(ReadOnlySpan{byte})"/> takes it, or 0 for stamped code.</param>
/// <param name="Length">The code's length in bytes.</param>
internal readonly record struct IlProfileKey(ValueHash256 Keccak, long Content, int Length)
{
    public static IlProfileKey ByHash(in ValueHash256 keccak, int length) => new(keccak, 0, length);

    public static IlProfileKey ByContent(long content, int length) => new(default, content, length);

    /// <summary>Whether the key is a content hash, so a code info's bytes must match a contract's before its state takes it.</summary>
    public bool IsByContent => Keccak == default;
}

/// <summary>What the runtime keeps of one code content, whichever code infos carry it: its compiled contracts and the list's word on it.</summary>
/// <remarks>
/// Partial: the visit gate's learned visit lengths and live profiling's counters and scorer fields are declared beside
/// the code that keeps them. A profile holds no code info; only code no cache stamped keeps its bytes, in <see cref="Retained"/>.
/// </remarks>
internal sealed partial class IlCodeProfile(IlProfileKey key, int size)
{
    private static int _lastId;

    /// <summary>A small number unique to the profile, for what is salted or hashed per contract.</summary>
    public readonly int Id = Interlocked.Increment(ref _lastId);

    public readonly IlProfileKey Key = key;

    /// <summary>The code's size in bytes.</summary>
    public readonly int Size = size;

    /// <summary>The list's <c>minv=</c>: the log2 of the visit length from which block processing's runner admits the contract, or 0 for the runner's default.</summary>
    public byte BlockMinVisitLog2;

    /// <summary>The list's <c>use=</c>: which kinds of thread may run the shipped contract, as the bits of <see cref="IlRuntime.RunAll"/>; all of them for an entry without it.</summary>
    public byte UseMask = IlRuntime.RunAll;

    /// <summary>
    /// Whether the list vouches for the shipped contract on the read-only queries' runner, with <c>qminv=</c>: the visit
    /// gate admits its visits of a key not learned yet from their first part rather than renting them, and with
    /// <see cref="QueryMinVisitLog2"/> 0 every visit, learning nothing. Its first calls then run compiled, as later ones do.
    /// </summary>
    public bool QuerySeeded;

    /// <summary>The list's <c>qminv=</c>, where <see cref="QuerySeeded"/>: the log2 of the visit length from which the queries' runner admits the contract from a visit's first part; 0 for every visit.</summary>
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

    /// <summary>The shipped contract compiled ahead for the next fork's body set, which a fork miss attaches; null until a lookahead compiles one.</summary>
    public volatile IlEvm.IlContract? ShippedNext;

    /// <summary>The contract live profiling promoted, for the source its <see cref="IlEvm.IlContract.Origin"/> names; null while there is none.</summary>
    public volatile IlEvm.IlContract? Live;
}

/// <summary>A code slot's mark that a runner has nothing compiled for the code: the runner declines its frames without reading anything the slot points at.</summary>
/// <remarks>
/// <see cref="IlRuntime.QuietBlock"/> and <see cref="IlRuntime.QuietQuery"/> mark one runner, <see cref="IlRuntime.QuietAll"/>
/// both. When what a runner may run grows, the marks that cover it are replaced by new ones, so that every slot marked
/// with an old one resolves once more. The hot path only compares a mark; a runner that meets an old one reads it, to
/// see whether only the other runner's growth made it old.
/// </remarks>
/// <param name="block">Whether the mark says block processing's runner has nothing for the code.</param>
/// <param name="query">Whether it says the read-only queries' runner has nothing.</param>
/// <param name="blockGeneration">The growths of what block processing's runner may run when the mark was made.</param>
/// <param name="queryGeneration">The growths of what the read-only queries' runner may run.</param>
internal sealed class IlQuiet(string name, bool block, bool query, int blockGeneration, int queryGeneration)
{
    public readonly bool Block = block;
    public readonly bool Query = query;
    public readonly int BlockGeneration = blockGeneration;
    public readonly int QueryGeneration = queryGeneration;

    /// <summary>Whether the mark still holds for one runner: it covers it, and what that runner may run has not grown since.</summary>
    public bool Holds(bool block) => block
        ? Block && BlockGeneration == IlRuntime.BlockGeneration
        : Query && QueryGeneration == IlRuntime.QueryGeneration;

    public override string ToString() => name;
}

/// <summary>
/// The Adaptive rules' lookup: which compiled contract, if any, each runner runs a code on, resolved once per code info
/// and runtime version and kept in the code's slot.
/// </summary>
/// <remarks>
/// <para>
/// Every code content the runtime knows has an <see cref="IlCodeProfile"/>: one per list entry, published at startup,
/// and one for each code a counting runner meets, for live profiling. A code info's slot holds an <see cref="IlEvm.IlCodeState"/>
/// naming each runner's contract, or an <see cref="IlQuiet"/> mark where a runner has none, so the hot path compares
/// one field, or follows one reference. One state serves both runners: a code cache hands both the same code infos.
/// </para>
/// <para>
/// <see cref="Version"/> moves whenever what may run changes - a list entry published, a promotion, a release - and a
/// state resolved at an older one resolves again on its next part, once. A runner's marks are replaced only when what
/// it may run grows; a shrink leaves marked code marked, rightly.
/// </para>
/// <para>
/// Partial: live profiling keeps its epoch and its tables beside the code that writes them.
/// </para>
/// </remarks>
internal static partial class IlRuntime
{
    /// <summary>The <see cref="IlEvm.IlContract.RunMask"/> bit of block processing's own thread, as the core marks it for each processing pass.</summary>
    public const byte RunMain = 1;

    /// <summary>The bit of the other threads block processing's runner serves: prewarming, production, the mempool's session.</summary>
    public const byte RunWorkers = 2;

    /// <summary>The bit of the read-only queries' runner.</summary>
    public const byte RunQuery = 4;

    public const byte RunAll = RunMain | RunWorkers | RunQuery;

    /// <summary>
    /// Set beside a run mask's cleared bits while block processing's switchback check keeps its runner off the contract,
    /// for a second after the loss: the parts its runner declines then are looked at for a visit under way (<see cref="IlCheck"/>).
    /// </summary>
    public const byte CheckOffBlock = 0x10;

    /// <summary>As <see cref="CheckOffBlock"/>, for the read-only queries' runner's check.</summary>
    public const byte CheckOffQuery = 0x20;

    /// <summary>Moves on every change to what may run; a state resolved at an older one resolves again.</summary>
    public static int Version = 1;

    /// <summary>The mark of code block processing's runner has nothing for.</summary>
    public static IlQuiet QuietBlock = new("quiet: block", block: true, query: false, 0, 0);

    /// <summary>The mark of code the read-only queries' runner has nothing for.</summary>
    public static IlQuiet QuietQuery = new("quiet: query", block: false, query: true, 0, 0);

    /// <summary>The mark of code neither runner has anything for, so code both run is not marked back and forth.</summary>
    public static IlQuiet QuietAll = new("quiet: both", block: true, query: true, 0, 0);

    /// <summary>Whether the read-only queries' runner counts the parts it is offered for live profiling: Adaptive with <see cref="IlEvmPrefer.Rpc"/>.</summary>
    /// <remarks>
    /// A counting runner stops at a mark but in the transactions it samples (<see cref="IlThread.TopLevelEntry"/>): code a
    /// sampled transaction meets gets a state, and a profile, for its counts. One state serves both runners, as the code
    /// infos are shared, so block processing's parts of code the queries counted read that state rather than a mark: one
    /// line of it, its first, until a new version returns code with nothing to run or count to its mark.
    /// </remarks>
    public static bool QueryCounting;

    /// <summary>Whether block processing's runner counts them: with <see cref="IlEvmPrefer.Blocks"/>, while a profiled branch is processed.</summary>
    /// <remarks>Set by the scheduler at a branch's start, for a branch of at most two blocks while not syncing, and cleared at its completion.</remarks>
    public static volatile bool BlocksCounting;

    /// <summary>Live profiling's epoch for block processing: the blocks of the branches it counted, one at each block's start, moved by the scheduler.</summary>
    public static int BlockEpoch;

    /// <summary>Live profiling's epoch for the read-only queries: the seconds since the process started, or the one a test set.</summary>
    public static int RpcEpoch => ManualRpcEpoch >= 0 ? ManualRpcEpoch : (int)((Environment.TickCount64 - IlEvmSettings.ProcessStartedAt) / 1000);

    /// <summary>For tests: the read-only queries' epoch in place of the clock's, while not negative.</summary>
    internal static int ManualRpcEpoch = -1;

    /// <summary>
    /// Whether block processing is processing a branch: set when it starts one and cleared when it completes it, by the
    /// scheduler (<see cref="IlScheduler"/>), from the main processing context's branch events. While it is, block
    /// processing's runner's checks take samples on every thread it serves, prewarming's too; outside it only block
    /// processing's own thread's processing passes do, so the mempool's session and block production never time a contract.
    /// </summary>
    public static volatile bool InBranch;

    /// <summary>The kinds of block-processing thread <see cref="IIlEvmConfig.BlockThreads"/> lets run compiled code, as <see cref="RunMain"/> and <see cref="RunWorkers"/> bits.</summary>
    public static int BlockThreadsMask = RunMain | RunWorkers;

    /// <summary>The most profiles kept, shipped ones included; past it, code a counting runner meets gets none, and its frames are declined, while a list entry always gets one.</summary>
    public static int MaxProfiles { get; set; } = 32_768;

    /// <summary>The most bytes of code no cache stamped the profiles keep for live compiles.</summary>
    public static long MaxRetainedBytes { get; set; } = 16L << 20;

    /// <summary>Every profile, by code identity.</summary>
    public static readonly ConcurrentDictionary<IlProfileKey, IlCodeProfile> Profiles = new();

    /// <summary>The list file's <c># calib</c> lines, without the prefix: the constants the model and the block runner's gate take from the Stats runs.</summary>
    public static IReadOnlyList<string> Calibration { get; set; } = [];

    private static readonly object _publishing = new();

    /// <summary>The shipped profiles by their code's length, for code no cache stamped, which their bytes then decide; copied on write.</summary>
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

    /// <summary>How often what the read-only queries' runner may run grew.</summary>
    public static int QueryGeneration => Volatile.Read(ref _queryGeneration);

    /// <summary>The profiles kept, shipped ones included.</summary>
    public static int ProfileCount => Volatile.Read(ref _profiles);

    /// <summary>The bytes of code the profiles keep, against <see cref="MaxRetainedBytes"/>.</summary>
    public static long RetainedBytes => Interlocked.Read(ref _retainedBytes);

    /// <summary>The IL and tables of the shipped contracts published so far.</summary>
    public static long ShippedBytes => Interlocked.Read(ref _shippedBytes);

    /// <summary>Code states created for code a runner met, and states resolved again at a newer version.</summary>
    public static long StatesResolved => Interlocked.Read(ref _statesResolved);

    public static long StatesRefreshed => Interlocked.Read(ref _statesRefreshed);

    /// <summary>Resolutions that gave the resolving runner a contract for its frame's body set.</summary>
    public static long StatesAttached => Interlocked.Read(ref _statesAttached);

    /// <summary>Content hashes taken of code no cache stamped, at most one per state.</summary>
    public static long ContentHashes => Interlocked.Read(ref _contentHashes);

    /// <summary>Lookups of a code among the profiles, at most one per resolution.</summary>
    public static long Lookups => Interlocked.Read(ref _lookups);

    /// <summary>The shipped profiles by code length, as published.</summary>
    internal static IReadOnlyDictionary<int, IlCodeProfile[]>? ShippedLengthIndex => Volatile.Read(ref _shippedByLength);

    /// <summary>Resolves a code info whose slot holds no state yet: <paramref name="seen"/>, nothing or another runner's or an old mark.</summary>
    /// <param name="counting">Whether the runner counts for live profiling, so profiles of code no cache stamped exist to be found by its content.</param>
    /// <param name="sampling">Whether the transaction is sampled for live profiling: code with no profile gets one, and code with nothing to run a state for its counts.</param>
    /// <returns>The state, installed in the slot unless another thread's got there first, then that one; or null, the slot marked, when the runner declines the code.</returns>
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
        // Block processing's code comes from the code database stamped with its keccak; whatever else it meets is left
        // to the interpreter unhashed.
        if (block && codeInfo.CodeHash == default)
            return Mark(codeInfo, seen, own);
        // A mark made old only by what the other runner may run still holds for this one: the slot takes this runner's
        // own mark, with no lookup, and the other runner resolves the code when it meets it.
        if (!sampling && seen is IlQuiet old && old.Holds(block))
            return Mark(codeInfo, seen, own);

        long content = 0;
        bool hasContent = false;
        IlCodeProfile? profile = Lookup(codeInfo, counting, hashing: true, ref content, ref hasContent) ??
            (sampling ? Create(codeInfo, content, hasContent, block) : null);
        IlEvm.IlContract? blockRun = profile is null ? null : RunFor(profile, codeInfo, block: true, bodies);
        IlEvm.IlContract? queryRun = profile is null ? null : RunFor(profile, codeInfo, block: false, bodies);
        IlEvm.IlContract? run = block ? blockRun : queryRun;
        // Nothing to run and nothing to count: marked, so the runner declines the code's frames from the slot alone;
        // for both runners when the other has nothing either. Code with no profile has nothing to count, as past the
        // table's cap.
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
    /// <returns>
    /// The state; or null for a part the runner declines, as when no runner has anything left to run or count for the
    /// code, after a release, and the slot takes the mark for both again.
    /// </returns>
    /// <param name="counting">Whether the runner counts for live profiling, as for <see cref="Resolve"/>.</param>
    /// <param name="sampling">Whether the transaction is sampled for live profiling, as for <see cref="Resolve"/>.</param>
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
        // Nothing to run for either runner, and nothing to count: with no profile, or with no runner counting. A counting
        // runner's next sampled transaction resolves the code from the mark again, and makes its profile then; a state
        // kept with none would keep it from ever having one.
        if (blockRun is null && queryRun is null && (profile is null || (!QueryCounting && !BlocksCounting)) && state.Contract is null && state.Shipped is null)
        {
            // A state with nothing in it would cost every part a read of it; the mark costs none. One the Legacy rules
            // compiled for, in a process that switched rules, is kept.
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
    /// The state keeps its contract on a miss, so parts of the body set it was compiled for still run it, and records the
    /// body set it missed, so later parts of it go to the interpreter without coming here, until the state resolves again;
    /// the profile takes it as the body set its code runs on now, which a live compile is for. A contract compiled ahead
    /// for the next fork attaches here, with no new version, at the fork's first part; one published for a body set
    /// parts already missed must move the version, as every publish does.
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

    /// <summary>Parts that missed their state's contract's body set and looked for another; once per state and body set, until it resolves again or something is compiled ahead.</summary>
    public static long ForkMisses => Interlocked.Read(ref _forkMisses);

    /// <summary>
    /// Publishes a contract compiled ahead for the next fork's body set, as the fork lookahead will: its first part on that
    /// body set attaches it (<see cref="ForkMiss"/>), with no new version, even where parts of it already found nothing.
    /// </summary>
    public static void PublishNext(IlCodeProfile profile, IlEvm.IlContract contract)
    {
        contract.Profile = profile;
        profile.ShippedNext = contract;
        Interlocked.Increment(ref MissEpoch);
    }

    /// <summary>The contract a runner may run the profile's code on for <paramref name="bodies"/>, or null.</summary>
    /// <remarks>
    /// Block processing runs the shipped contracts and those live profiling of blocks promoted, and none at all under
    /// <see cref="IlEvmSettings.BlockDeclineAll"/>; the read-only queries run any.
    /// </remarks>
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

    /// <summary>Any contract a runner may run the profile's code on, whatever its body set, or null when it has none.</summary>
    private static IlEvm.IlContract? AnyFor(IlCodeProfile profile, bool block)
    {
        if (block && IlEvmSettings.BlockDeclineAll)
            return null;
        if ((profile.Shipped ?? profile.ShippedNext) is { } shipped)
            return shipped;
        IlEvm.IlContract? live = profile.Live;
        return live is not null && (!block || live.Origin == IlOrigin.LiveBlocks) ? live : null;
    }

    /// <summary>
    /// What a state names for a runner: the contract for the frame's body set, else any it may run, so that a part of
    /// that one's body set runs it and the others find theirs through <see cref="ForkMiss"/>; null when it may run none.
    /// </summary>
    private static IlEvm.IlContract? RunFor(IlCodeProfile profile, CodeInfo codeInfo, bool block, OpcodeBodySet bodies)
    {
        IlEvm.IlContract? contract = RunnableFor(profile, block, bodies) ?? AnyFor(profile, block);
        return contract is not null && Matches(profile, contract, codeInfo) ? contract : null;
    }

    /// <summary>Whether <paramref name="codeInfo"/>'s code is the contract's: given by the keccak, compared byte for byte for a profile keyed by content.</summary>
    private static bool Matches(IlCodeProfile profile, IlEvm.IlContract contract, CodeInfo codeInfo) =>
        !profile.Key.IsByContent || codeInfo.CodeSpan.SequenceEqual(contract.Code.Span);

    /// <summary>The profile of the code, if the runtime keeps one: by the stamped keccak, else by content, a shipped one first.</summary>
    /// <param name="hashing">Whether the content of code no cache stamped may be hashed here; a hash already taken is used either way.</param>
    private static IlCodeProfile? Lookup(CodeInfo codeInfo, bool counting, bool hashing, ref long content, ref bool hasContent)
    {
        Interlocked.Increment(ref _lookups);
        int length = codeInfo.CodeSpan.Length;
        ValueHash256 hash = codeInfo.CodeHash;
        if (hash != default)
            return Profiles.TryGetValue(IlProfileKey.ByHash(in hash, length), out IlCodeProfile? stamped) ? stamped : null;

        // Code no cache stamped, as from a state override: among the shipped contracts by content, then among the
        // profiles live profiling keeps for such code, which exist only where a runner counts.
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
    /// The shipped profile of code no cache stamped: among the shipped codes as long, the one its bytes equal; never
    /// found by its keccak, which takes tens of microseconds over large code, nor by a hash of its content, which reads
    /// it all as well: once per code info, and a state override's code info is new on every call its code cache misses.
    /// </summary>
    /// <remarks>A shipped code of the same length but other content differs within its first words, mostly, where the compare stops.</remarks>
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

    /// <summary>A profile for code a counting runner met, unless the table is full; code no cache stamped also keeps its bytes while the budget allows.</summary>
    /// <param name="block">Whether block processing's runner counts, in whose epochs the profile is made: from then it counts as untouched, until counted.</param>
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

    /// <summary>
    /// Lets go of a profile live profiling found not worth keeping, near the table's cap: the table forgets it, and the
    /// bytes it retained go back to the budget. States that hold it keep counting into it until they resolve again,
    /// when a counting runner makes the code a new one.
    /// </summary>
    /// <returns>Whether the table held it.</returns>
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

        // Past the budget: the profile still counts, but has nothing a live compile could compile; a later state of the
        // code tries again.
        Interlocked.Add(ref _retainedBytes, -code.Length);
        Volatile.Write(ref profile.Retaining, 0);
    }

    /// <summary>Marks the slot with <paramref name="mark"/> over nothing or a mark, and declines the part.</summary>
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

    /// <summary>A state took <paramref name="contract"/> for a frame: its clock, for the check's delay, starts at its first frame, as the start-up window does.</summary>
    private static void Attached(IlEvm.IlContract contract)
    {
        Interlocked.Increment(ref _statesAttached);
        if (contract.AttachedAt == 0)
            contract.AttachedAt = Environment.TickCount64;
        IlEvmSettings.SawFrame();
    }

    /// <summary>
    /// Publishes contracts compiled for list entries: each entry's profile takes its contract, its metadata and the
    /// contract's run mask; the content index and the Legacy rules' map take them in; and every runner resolves its
    /// marked code and its states again.
    /// </summary>
    /// <remarks>Adds to what was published, as the windows after the startup compile do with the entries it left.</remarks>
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
                // Each runner whose switchback check is on times the contract against the interpreter once it runs.
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

    /// <summary>What the runners may run grew: the marks that cover those that grew are replaced, and every state resolves again.</summary>
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

    /// <summary>Forgets every profile and what was published, for tests; code infos resolved before resolve again.</summary>
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
