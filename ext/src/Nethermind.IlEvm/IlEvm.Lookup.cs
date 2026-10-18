// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    /// <summary>The compiler's state for one <see cref="CodeInfo"/>, in the frame code runner's per-code slot; laid out by hand so the fields the query runners' entry reads share the cache line its type check reads.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal sealed class IlCodeState
    {
        /// <summary>
        /// Legacy: what the query runners' entry does with a part of the code with no lookup, while <see cref="DecidedEpoch"/>
        /// is <see cref="IlEvmSettings.LegacyEpoch"/> and the part's body set is the contract's. With <see cref="DecidedUntil"/>
        /// 0 the contract runs the part, which it is decided for only once nothing but the settings can change that: its
        /// self-check kept it (a final verdict), or the self-check is off. Otherwise the part goes to the interpreter, the
        /// self-check having given the contract back (<see cref="long.MaxValue"/>) or put it on probation until that time
        /// (<see cref="Environment.TickCount64"/> milliseconds). Null: nothing was compiled for the code when it was decided,
        /// and while <see cref="Contract"/> and <see cref="Shipped"/> stay null the parts not drawn for counting go to the
        /// interpreter. Only the query runners read or write it: block processing's runner keeps its own rules on every part.
        /// </summary>
        /// <remarks>Written by the runners' slow paths only, field by field: a part that reads a mix of two decisions runs as either says, and compiled code gives the interpreter's results whichever runs.</remarks>
        [FieldOffset(0)] public IlContract? Decided;
        [FieldOffset(8)] public long DecidedUntil;
        [FieldOffset(16)] public int DecidedEpoch;
        [FieldOffset(20)] public int Executions;
        [FieldOffset(24)] public volatile IlContract? Contract;
        /// <summary>The precompiled contract for this code, kept apart so a run-time compile for another fork cannot displace it.</summary>
        [FieldOffset(32)] public volatile IlContract? Shipped;
        [FieldOffset(40)] public volatile IlFork? FailedFor;
        /// <summary>Whether the code was looked up among the precompiled contracts, which happens on its first frame of a fork they were compiled for.</summary>
        [FieldOffset(48)] public bool PrecompileChecked;
        [FieldOffset(49)] public bool HasContentHash;
        /// <summary>Legacy: whether the code came with a request or from the chain (<see cref="IsRequestCode"/>), 0 until a query's frame asked.</summary>
        [FieldOffset(50)] public byte Origin;
        /// <summary>
        /// The temperature gate's verdict for the trace runner (<see cref="IlTemperature"/>), apart from the decisions every
        /// query runner shares: <see cref="IlTemperature.GateEpoch"/> lets the code's trace parts take the shared decisions,
        /// its complement sends them to the interpreter, anything else sends them out of line to be judged. 0, a fresh
        /// state's, lets them take the shared decisions while the gate is off. Only the trace runner reads or writes it.
        /// </summary>
        [FieldOffset(52)] public int TraceGate;
        /// <summary>Interpreted opcodes counted toward hotness; a long, as sampled frames count with a weight and busy code runs billions.</summary>
        [FieldOffset(56)] public long OpCodesRun;
        [FieldOffset(64)] public long WindowStart;
        /// <summary>Until when a hot frame does not ask for a compile again, after one was turned away by a budget.</summary>
        [FieldOffset(72)] public long RetryAfter;
        /// <summary>A fast 64-bit hash of the code, taken on its second frame, keying the shared cache and the records of a request's code.</summary>
        [FieldOffset(80)] public long ContentHash;
        /// <summary>Opcodes the interpreter ran for this code in the current one-second window.</summary>
        [FieldOffset(88)] public int WindowOpCodes;
        [FieldOffset(92)] public int WindowFrames;
        [FieldOffset(96)] public int Pending;
        [FieldOffset(104)] public string? FailureReason;
        /// <summary>
        /// The temperature gate (<see cref="IlTemperature"/>): the opcodes and the frame parts its samples measured for the
        /// code in window <see cref="TempWindow"/>, each times its weight, and in the window before that one. Written by
        /// samples only, racily: a lost add only misjudges the code until its next sample. Far from the fields the entry
        /// reads, so their line stays shared.
        /// </summary>
        [FieldOffset(112)] public long TempOps;
        [FieldOffset(120)] public long PrevOps;
        [FieldOffset(128)] public long TempParts;
        [FieldOffset(136)] public long PrevParts;
        [FieldOffset(144)] public int TempWindow;
    }

    /// <summary>A fork's opcode semantics as the compiler sees them, one per interned <see cref="OpcodeBodySet"/>, so specs and forks running the same bodies share compiled contracts.</summary>
    internal sealed class IlFork(IlOp[] ops, OpcodeBodySet bodies)
    {
        public readonly IlOp[] Ops = ops;
        public readonly OpcodeBodySet Bodies = bodies;
        public bool UsesCallDestinations => Bodies.UsesCallDestinations;
    }

    private static readonly ConditionalWeakTable<OpcodeBodySet, IlFork> _ilForks = [];

    [ThreadStatic] private static OpcodeBodySet? _lastBodies;
    [ThreadStatic] private static IlFork? _lastFork;

    internal static IlFork IlGetFork(OpcodeBodySet bodies)
    {
        if (ReferenceEquals(bodies, _lastBodies))
            return _lastFork!;
        IlFork fork = _ilForks.GetValue(bodies, static b => new IlFork(IlDescribeOps(b), b));
        _lastBodies = bodies;
        _lastFork = fork;
        return fork;
    }

    internal readonly record struct IlContractKey(long Code, int Length, IlFork Fork);

    /// <summary>Compiled contracts by code content and fork, so a new <see cref="CodeInfo"/> of known code needs no compile.</summary>
    private static readonly ConcurrentDictionary<IlContractKey, IlContract> _ilContracts = new();
    /// <summary>The cache's keys, oldest pass first, and the IL their contracts hold together.</summary>
    private static readonly ConcurrentQueue<IlContractKey> _ilCacheOrder = new();
    private static long _ilCachedBytes;

    /// <summary>Keeps <paramref name="contract"/> for reuse by content, letting the least recently used go past the cache's bounds.</summary>
    /// <remarks>
    /// Second chance, a clock over the keys: past the bounds the oldest key goes unless a new code info found its contract
    /// since its last pass, which sends it round again. A stream of new contracts, as of code requests bring, so pushes
    /// out the contracts no code info asks for, not those that keep being found; a contract let go stays compiled where held.
    /// </remarks>
    private static void IlCache(IlContractKey key, IlContract contract)
    {
        if (!_ilContracts.TryAdd(key, contract))
            return;
        _ilCacheOrder.Enqueue(key);
        long cached = Interlocked.Add(ref _ilCachedBytes, contract.MemoryBytes);
        // Each key gets at most one more pass per call: a pass clears what it spares, so the next one evicts.
        int spared = 0;
        while ((cached > IlEvmSettings.MaxCachedIlBytes || _ilContracts.Count > IlEvmSettings.MaxCachedContracts) &&
               _ilCacheOrder.TryDequeue(out IlContractKey oldest))
        {
            if (!_ilContracts.TryGetValue(oldest, out IlContract? candidate))
                continue;
            if (candidate.CacheUsed && spared++ < _ilContracts.Count)
            {
                candidate.CacheUsed = false;
                _ilCacheOrder.Enqueue(oldest);
                continue;
            }

            if (_ilContracts.TryRemove(new KeyValuePair<IlContractKey, IlContract>(oldest, candidate)))
                cached = Interlocked.Add(ref _ilCachedBytes, -candidate.MemoryBytes);
        }
    }

    /// <summary>Empties the shared cache, for tests.</summary>
    internal static void ClearSharedCache()
    {
        _ilContracts.Clear();
        _ilCacheOrder.Clear();
        Interlocked.Exchange(ref _ilCachedBytes, 0);
    }

    /// <remarks>Two AES-based hashes, of the whole code and of its second half; a collision only costs a cache miss.</remarks>
    internal static long IlContentHash(CodeInfo codeInfo) => IlContentHash(codeInfo.CodeSpan);

    internal static long IlContentHash(ReadOnlySpan<byte> code) =>
        ((long)code.FastHash() << 32) | (uint)code[(code.Length / 2)..].FastHash();

    /// <summary>The compiled contract to run a frame of <paramref name="codeInfo"/> on, or null for the interpreter; counts the frame toward hotness and requests a compile once hot.</summary>
    /// <param name="count">Whether the interpreter should report the frame's opcodes, which counts it toward hotness.</param>
    /// <param name="drawn">The caller drew the part for counting already (<see cref="SkipsCount"/>), so it is not drawn again.</param>
    /// <param name="query">The machine of the read-only query the frame runs in, whose request counts toward the hotness of code that came with a request; null elsewhere.</param>
    /// <param name="gated">The trace runner asks with the temperature gate on: a compile also waits for the code to be hot in traces.</param>
    internal static IlContract? Find(CodeInfo codeInfo, OpcodeBodySet bodies, out IlCodeState state, out bool count, bool drawn = false,
        VirtualMachine<EthereumGasPolicy>? query = null, bool gated = false)
    {
        ref object? slot = ref IlEvmFrameRunner.CodeSlot(codeInfo);
        if (slot is not IlCodeState existing)
            existing = NewState(ref slot);

        state = existing;
        count = false;
        IlContract? contract = state.Contract;
        if (IlEvmDiagnostics.Enabled)
        {
            Interlocked.Increment(ref IlEvmDiagnostics.Finds);
            if (contract is not null && ReferenceEquals(contract.Bodies, bodies))
                Interlocked.Increment(ref IlEvmDiagnostics.DirectHits);
        }

        // A contract is compiled for one fork, the one of its body set (IlGetFork makes one per set), so the sets compare.
        if (contract is not null && ReferenceEquals(contract.Bodies, bodies))
            return contract;

        IlContract? shipped = state.Shipped;
        if (shipped is not null && ReferenceEquals(shipped.Bodies, bodies))
            return shipped;

        IlFork fork = IlGetFork(bodies);
        if (!state.PrecompileChecked && Attach(codeInfo, state, fork) is { } precompiled)
            return precompiled;

        if (ReferenceEquals(state.FailedFor, fork))
            return null;

        // Code no cache stamped, a state override's, longer than any deployment may leave is no contract's: the request
        // made it up, and is never compiled; its frames are not counted either.
        if (codeInfo.CodeHash == default && codeInfo.CodeSpan.Length > MaxRequestCodeLength)
        {
            state.FailureReason = $"code no cache stamped of {codeInfo.CodeSpan.Length} bytes, past {MaxRequestCodeLength}";
            state.FailedFor = fork;
            IlEvmSettings.CountFailed(state.FailureReason);
            return null;
        }

        // Past its second frame, code counts on one random frame in HotnessSampling, with that weight.
        int weight = SamplingWeight;
        if (!drawn && weight > 1 && state.Executions >= 2 && (NextHotnessRandom() & (uint)(weight - 1)) != 0)
            return null;

        count = true;
        int frames = state.Executions < 2 ? 1 : weight;
        int executions = state.Executions += frames;
        // Sampled, the rate rule needs two counted frames in its window: one large weighted frame is not steady use.
        bool hot = (executions >= IlEvmSettings.Threshold || state.OpCodesRun >= IlEvmSettings.OpcodeThreshold) &&
            (IlEvmSettings.Threshold <= 1 || (state.WindowOpCodes >= IlEvmSettings.HotOpcodesPerSecond && (weight == 1 || state.WindowFrames >= 2)));
        // A second frame, or the first hot one, looks the code up by content: it may be compiled under another code info,
        // or have failed to compile there, which this one then takes. A query's code no cache stamped does so on its first:
        // its hotness is its content's from its first frame, and a new code info of it, as a state override's gets whenever
        // the core's override cache lost it, runs compiled from its first part.
        if (executions == 2 || hot || (executions == 1 && query is not null && codeInfo.CodeHash == default && IlEvmSettings.Threshold > 1))
        {
            if (IlShared(codeInfo, state, fork) is { } shared)
                return shared;
            if (TakeKnownFailure(state, codeInfo.CodeSpan.Length, fork))
                return null;
        }

        // Code a query brought is hot by its content, over all its code infos, and only across requests.
        if (query is not null && IlEvmSettings.Threshold > 1 && state.HasContentHash && IsRequestCode(codeInfo, state))
            hot = HotByContent(codeInfo, state, query, frames);

        // The temperature gate holds back a trace's compile of code that is not hot in traces, as it would not run it there.
        if (hot && gated && !IlTemperature.IsHot(codeInfo, state))
        {
            IlSurfaceCounts.OfThisThread()[IlSurfaceCounts.HeldBackCompiles]++;
            return null;
        }

        return hot && (state.RetryAfter == 0 || Environment.TickCount64 >= state.RetryAfter) ? IlRequest(codeInfo, state, fork) : null;
    }

    [ThreadStatic] private static uint _hotnessRandom;

    /// <summary>The thread's next draw for hotness counting: a random choice rather than every nth frame, which frames that alternate between codes would alias.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NextHotnessRandom()
    {
        uint random = _hotnessRandom == 0 ? (uint)Environment.CurrentManagedThreadId * 2654435761u | 1 : _hotnessRandom;
        random ^= random << 13;
        random ^= random >> 17;
        random ^= random << 5;
        _hotnessRandom = random;
        return random;
    }

    /// <summary>Draws a part of code past its second frame for counting toward hotness, as <see cref="Find"/> does: true for the parts not counted, all but one in <see cref="SamplingWeight"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool SkipsCount()
    {
        int weight = SamplingWeight;
        return weight > 1 && (NextHotnessRandom() & (uint)(weight - 1)) != 0;
    }

    /// <summary>A new state, published over nothing; one another thread published first wins, and over anything else it serves the frame unpublished.</summary>
    private static IlCodeState NewState(ref object? slot)
    {
        IlCodeState created = new();
        return Interlocked.CompareExchange(ref slot, created, null) as IlCodeState ?? created;
    }

    /// <summary>The weight of a counted frame: <see cref="IlEvmSettings.HotnessSampling"/>, or 1 with diagnostics on, which report every frame.</summary>
    internal static int SamplingWeight => IlEvmDiagnostics.Enabled ? 1 : IlEvmSettings.HotnessSampling;

    /// <summary>
    /// The contract a block frame runs when block processing compiles nothing at run time: the shipped one for the part's
    /// body set, else null; never one compiled at run time, for a query or otherwise, though the code state is shared. No counting.
    /// </summary>
    /// <remarks>So block processing runs compiled only what the startup list vouches for, and two Stats runs' block frames differ only by their lists.</remarks>
    internal static IlContract? FindShippedOnly(CodeInfo codeInfo, OpcodeBodySet bodies)
    {
        ref object? slot = ref IlEvmFrameRunner.CodeSlot(codeInfo);
        if (slot is IlCodeState state)
        {
            IlContract? shipped = state.Shipped;
            if (shipped is null && state.PrecompileChecked)
                return null;
            if (shipped is not null && ReferenceEquals(shipped.Bodies, bodies))
                return shipped;
            return state.PrecompileChecked ? null : Attach(codeInfo, state, IlGetFork(bodies));
        }

        if (Volatile.Read(ref _precompiled) is null)
            return null;
        return Attach(codeInfo, NewState(ref slot), IlGetFork(bodies));
    }

    /// <summary>Attaches the precompiled contract for <paramref name="fork"/>; the code is marked checked unless shipped for another fork, whose frames look again.</summary>
    private static IlContract? Attach(CodeInfo codeInfo, IlCodeState state, IlFork fork)
    {
        IlContract? precompiled = Precompiled(codeInfo, state, fork, out bool otherFork);
        if (precompiled is null)
        {
            if (!otherFork)
                state.PrecompileChecked = true;
            return null;
        }

        state.Shipped = precompiled;
        if (state.Contract is null)
            state.Contract = precompiled;
        state.PrecompileChecked = true;
        // Laid out at startup: its self-check waits from its first frame, so the helpers it calls can tier up first.
        if (Interlocked.Increment(ref _precompiledAttached) > 0 && precompiled.Attached == 0 && Interlocked.Exchange(ref precompiled.Attached, 1) == 0)
            precompiled.CompiledAt = Environment.TickCount64;
        return precompiled;
    }

    private static long _precompiledAttached;

    /// <summary>Code states that took a precompiled contract on their first frame: how much of the shipped list running code met.</summary>
    public static long PrecompiledAttached => Interlocked.Read(ref _precompiledAttached);

    /// <summary>The contracts compiled from the shipped list, by the keccak of their code; null when there are none.</summary>
    private static Dictionary<ValueHash256, IlContract>? _precompiled;

    /// <summary>The same contracts by code length, to find code no cache stamped by its bytes; null when there are none.</summary>
    private static Dictionary<int, IlContract[]>? _shippedByLength;

    private static readonly object _publishing = new();

    /// <summary>Publishes contracts compiled for a list in place of whatever was published, for tests; the startup compile adds through <see cref="PublishShipped"/>.</summary>
    internal static void PublishPrecompiled(IReadOnlyDictionary<ValueHash256, IlContract> contracts)
    {
        if (contracts.Count == 0)
            return;
        ClearPrecompiled();
        PublishShipped([.. contracts.Select(static c => (new IlEvmPrecompileList.Entry(c.Key), c.Value))]);
    }

    /// <summary>Adds contracts compiled for list entries to those frames find by the keccak of their code, and by their bytes; copied on write so frames read the maps without a lock.</summary>
    internal static void PublishShipped(IReadOnlyList<(IlEvmPrecompileList.Entry Entry, IlContract Contract)> shipped)
    {
        if (shipped.Count == 0)
            return;
        lock (_publishing)
        {
            Dictionary<ValueHash256, IlContract> map = Volatile.Read(ref _precompiled) is { } published ? new(published) : [];
            Dictionary<int, IlContract[]> index = Volatile.Read(ref _shippedByLength) is { } lengths ? new(lengths) : [];
            foreach ((IlEvmPrecompileList.Entry entry, IlContract contract) in shipped)
            {
                // Each length's candidates once each, the list's first first: a hash published again keeps its place.
                IlContract[] candidates = index.TryGetValue(contract.Code.Length, out IlContract[]? known) ? known : [];
                int at = map.TryGetValue(entry.Hash, out IlContract? replaced) ? Array.IndexOf(candidates, replaced) : -1;
                index[contract.Code.Length] = at < 0 ? [.. candidates, contract] : [.. candidates[..at], contract, .. candidates[(at + 1)..]];
                map[entry.Hash] = contract;
            }

            Volatile.Write(ref _shippedByLength, index);
            Volatile.Write(ref _precompiled, map);
        }
    }

    /// <summary>
    /// The shipped contract whose code these unstamped bytes equal. Not by keccak or content hash: both read all the code,
    /// and a state override's code info is new on every call its code cache misses; a compare mostly stops early.
    /// </summary>
    internal static IlContract? ShippedByContent(CodeInfo codeInfo)
    {
        ReadOnlySpan<byte> code = codeInfo.CodeSpan;
        if (Volatile.Read(ref _shippedByLength) is not { } index || !index.TryGetValue(code.Length, out IlContract[]? candidates))
            return null;
        foreach (IlContract contract in candidates)
        {
            if (code.SequenceEqual(contract.Code.Span))
                return contract;
        }

        return null;
    }

    /// <summary>Forgets the precompiled contracts, for tests.</summary>
    internal static void ClearPrecompiled()
    {
        lock (_publishing)
        {
            Volatile.Write(ref _precompiled, null);
            Volatile.Write(ref _shippedByLength, null);
        }
    }

    /// <summary>How many contracts the startup compile published, for tests and diagnostics.</summary>
    public static int PrecompiledCount => Volatile.Read(ref _precompiled)?.Count ?? 0;

    /// <summary>Whether the startup compile published a contract for the code of keccak <paramref name="hash"/>.</summary>
    public static bool IsPrecompiled(in ValueHash256 hash) => Volatile.Read(ref _precompiled)?.ContainsKey(hash) == true;

    private static IlContract? Precompiled(CodeInfo codeInfo, IlCodeState state, IlFork fork, out bool otherFork)
    {
        otherFork = false;
        Dictionary<ValueHash256, IlContract>? map = Volatile.Read(ref _precompiled);
        if (map is null)
            return null;
        IlContract? contract;
        ValueHash256 hash = codeInfo.CodeHash;
        if (hash != default)
        {
            if (!map.TryGetValue(hash, out contract))
                return null;
        }
        else
        {
            // Code no cache stamped, as from a state override, is found by its bytes, never its keccak, which takes tens of microseconds on large code.
            contract = ShippedByContent(codeInfo);
            if (contract is null)
                return null;
        }

        if (ReferenceEquals(contract.Fork, fork))
            return contract;
        otherFork = true;
        return null;
    }

    /// <summary>Lays out <paramref name="code"/> for <paramref name="bodies"/> and JIT-compiles every chunk, for the startup compile; permanent, so it counts toward no budget.</summary>
    internal static IlContract? Precompile(byte[] code, OpcodeBodySet bodies, out string? reason)
    {
        IlContract? contract = IlCompiler.Compile(new CodeInfo(code), IlGetFork(bodies), out reason);
        if (contract is null)
            return null;
        contract.CompileAllChunks();
        // Never released: a verdict against it only keeps the runner whose self-check reached it off it.
        contract.Check.Name = contract.Name;
        return contract;
    }

    internal static OpcodeBodySet? BodySetFor(Nethermind.Core.Specs.IReleaseSpec spec) =>
        VirtualMachine<EthereumGasPolicy>.GetOpcodeBodySet(spec);

    /// <summary>How long code that a budget turned away waits before asking again.</summary>
    private const int RetryDelayMs = 5_000;

    /// <summary>The longest code a deployment may leave (EIP-170): the most a request's code may be and still be compiled, when no cache stamped it.</summary>
    internal const int MaxRequestCodeLength = 24 * 1024;

    /// <summary>The interpreter ran <paramref name="opCodes"/> opcodes of a frame of <paramref name="codeInfo"/>; what makes it hot.</summary>
    internal static void OnInterpreted(CodeInfo codeInfo, int opCodes)
    {
        if (IlEvmFrameRunner.CodeSlot(codeInfo) is not IlCodeState state)
            return;
        // Only sampled frames report past the second, weighted: a count can pass an int, so it is summed as a long and the window's saturates.
        long ran = opCodes;
        if (state.Executions > 2)
            ran *= SamplingWeight;
        state.OpCodesRun += ran;
        long now = Environment.TickCount64;
        if (now - state.WindowStart >= 1000)
        {
            state.WindowStart = now;
            state.WindowOpCodes = 0;
            state.WindowFrames = 0;
        }

        state.WindowOpCodes = (int)Math.Min(int.MaxValue, state.WindowOpCodes + ran);
        state.WindowFrames++;
        // A query's own code is hot by its content (HotByContent): what this code info ran counts there too.
        if (state.Origin == FromRequest && state.HasContentHash && RecordOf(state.ContentHash, codeInfo.CodeSpan.Length) is { } record)
            record.Ran(ran, now);
    }

    private static IlContract? IlShared(CodeInfo codeInfo, IlCodeState state, IlFork fork)
    {
        if (!state.HasContentHash)
        {
            long start = IlEvmDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            state.ContentHash = IlContentHash(codeInfo);
            state.HasContentHash = true;
            if (IlEvmDiagnostics.Enabled)
            {
                Interlocked.Increment(ref IlEvmDiagnostics.ContentHashes);
                Interlocked.Add(ref IlEvmDiagnostics.ContentHashBytes, codeInfo.CodeSpan.Length);
                Interlocked.Add(ref IlEvmDiagnostics.ContentHashTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
        }

        if (IlEvmDiagnostics.Enabled)
            Interlocked.Increment(ref IlEvmDiagnostics.SharedLookups);
        if (_ilContracts.TryGetValue(new IlContractKey(state.ContentHash, codeInfo.CodeSpan.Length, fork), out IlContract? contract) &&
            codeInfo.CodeSpan.SequenceEqual(contract.Code.Span))
        {
            if (IlEvmDiagnostics.Enabled)
                Interlocked.Increment(ref IlEvmDiagnostics.SharedHits);
            // Used: the cache's next pass over it spares it.
            if (!contract.CacheUsed)
                contract.CacheUsed = true;
            state.Contract = contract;
            return contract;
        }

        return null;
    }

    private static IlContract? IlRequest(CodeInfo codeInfo, IlCodeState state, IlFork fork)
    {
        if (IlEvmDiagnostics.Enabled)
            Interlocked.Increment(ref IlEvmDiagnostics.Requests);
        if (IlEvmSettings.CompileSynchronously)
            return IlCompileInto(codeInfo, state, fork);

        if (Interlocked.Exchange(ref state.Pending, 1) == 1)
            return null;

        // A request's code is hot by its content (HotByContent): one layout of it waits at a time, whichever code info asked.
        bool request = IsRequestCode(codeInfo, state);
        IlCodeRecord? record = request && state.HasContentHash ? RecordOf(state.ContentHash, codeInfo.CodeSpan.Length) : null;
        if (record is not null && Interlocked.Exchange(ref record.Pending, 1) == 1)
        {
            state.Pending = 0;
            return null;
        }

        // The hot order ranks the layout by the opcodes its code runs on the interpreter from now on, by content for a request's.
        if (!IlCompileQueue.Enqueue((Code: codeInfo, State: state, Fork: fork, Record: record, QueuedAt: record?.OpCodesRun ?? state.OpCodesRun),
                static s =>
                {
                    IlCompileInto(s.Code, s.State, s.Fork);
                    s.State.Pending = 0;
                    if (s.Record is not null)
                        s.Record.Pending = 0;
                },
                static s => (s.Record?.OpCodesRun ?? s.State.OpCodesRun) - s.QueuedAt, request))
        {
            state.RetryAfter = Environment.TickCount64 + RetryDelayMs;
            state.Pending = 0;
            if (record is not null)
                record.Pending = 0;
        }

        return null;
    }

    /// <summary>The codes being laid out now with several compile threads, so two code states of the same code do not both compile it.</summary>
    private static readonly ConcurrentDictionary<IlContractKey, byte> _ilCompiling = new();

    private static IlContract? IlCompileInto(CodeInfo codeInfo, IlCodeState state, IlFork fork)
    {
        bool claimed = false;
        IlContractKey key = default;
        try
        {
            if (IlShared(codeInfo, state, fork) is { } shared)
                return shared;
            if (TakeKnownFailure(state, codeInfo.CodeSpan.Length, fork))
                return null;

            if (IlEvmSettings.CompileThreads > 1)
            {
                // Another thread lays this code out; a later frame finds its contract by content.
                key = new IlContractKey(state.ContentHash, codeInfo.CodeSpan.Length, fork);
                if (!_ilCompiling.TryAdd(key, 0))
                    return null;
                claimed = true;
                if (IlShared(codeInfo, state, fork) is { } raced)
                    return raced;
            }

            if (IlEvmSettings.LiveIlBytes >= IlEvmSettings.MaxLiveIlBytes)
            {
                // Compiled code already takes all it may; the code asks again once some may have been collected.
                state.RetryAfter = Environment.TickCount64 + RetryDelayMs;
                IlEvmSettings.CountDeferred();
                return null;
            }

            IlContract? contract = IlCompiler.Compile(codeInfo, fork, out string? reason);
            if (contract is null)
            {
                Failed(codeInfo, state, fork, reason);
                return null;
            }

            // One thread overshoots the cap by at most the contract that passed the check above; several admit atomically, reserving as if its own bytes were let go, so they too overshoot by at most one.
            if (!claimed)
            {
                contract.MarkLive();
            }
            else if (!contract.TryMarkLive(freed: contract.MemoryBytes))
            {
                state.RetryAfter = Environment.TickCount64 + RetryDelayMs;
                IlEvmSettings.CountDeferred();
                return null;
            }

            if (IlEvmSettings.EntryChunk && contract.EntryChunk is { Length: > 0 } entries && entries[0] >= 0)
                contract.CompileChunk(entries[0]);
            // Both counts are sampled alike (IlEvm.OnInterpreted), so their ratio is the code's opcodes per part.
            contract.OpsPerPart = Math.Max(1, state.OpCodesRun / Math.Max(1, state.Executions));
            // Its chunks' JITs are paced as its layout was.
            contract.FromRequest = IsRequestCode(codeInfo, state);
            // A contract the self-check gives back keeps its verdict, not its code.
            contract.Check.OnGivenBack = contract.Release;
            contract.Check.Name = contract.Name;
            state.Contract = contract;
            IlCache(new IlContractKey(state.ContentHash, codeInfo.CodeSpan.Length, fork), contract);
            IlEvmSettings.CountCompiled();
            return contract;
        }
        catch (Exception exception)
        {
            Failed(codeInfo, state, fork, exception.ToString());
            return null;
        }
        finally
        {
            if (claimed)
                _ilCompiling.TryRemove(key, out _);
        }
    }

    /// <summary>Records that the code of <paramref name="state"/> failed to compile for <paramref name="fork"/>: in the state, and by content for its other code infos.</summary>
    private static void Failed(CodeInfo codeInfo, IlCodeState state, IlFork fork, string? reason)
    {
        state.FailureReason = reason;
        state.FailedFor = fork;
        if (state.HasContentHash)
            RecordFor(state.ContentHash, codeInfo.CodeSpan.Length).Failed(fork, reason);
        IlEvmSettings.CountFailed(reason);
    }

    /// <summary>Why the compiler last declined <paramref name="codeInfo"/>, for tests and diagnostics.</summary>
    public static string? FailureReason(CodeInfo codeInfo) =>
        IlEvmFrameRunner.CodeSlot(codeInfo) is IlCodeState state ? state.FailureReason ?? state.Contract?.ChunkFailure : null;

    /// <summary>The compiled methods' IL size, JIT time and chunk count for <paramref name="codeInfo"/>, for benchmarks and diagnostics.</summary>
    public static (int IlBytes, TimeSpan JitTime, int Chunks)? CompileStats(CodeInfo codeInfo) =>
        CompiledOf(codeInfo) is { } contract ? (contract.IlBytes, contract.JitTime, contract.CompiledChunks) : null;

    /// <summary>Whether <paramref name="codeInfo"/> has a compiled contract, for tests and diagnostics.</summary>
    public static bool IsCompiled(CodeInfo codeInfo) => CompiledOf(codeInfo) is not null;

    /// <summary>The contract of the code's state.</summary>
    private static IlContract? CompiledOf(CodeInfo codeInfo) => (IlEvmFrameRunner.CodeSlot(codeInfo) as IlCodeState)?.Contract;

    /// <summary>The self-check's verdicts so far in the process: kept compiled, put on probation, given back.</summary>
    public static (int Kept, int Probation, int GivenBack) SelfCheckVerdicts => IlSelfCheck.Verdicts;

    /// <summary>The IL of the contracts the shared cache holds, for tests and diagnostics.</summary>
    public static long CachedIlBytes => Interlocked.Read(ref _ilCachedBytes);

    /// <summary>The self-check's samples for <paramref name="codeInfo"/>, for diagnostics.</summary>
    public static string? DescribeSelfCheck(CodeInfo codeInfo) => (IlEvmFrameRunner.CodeSlot(codeInfo) as IlCodeState)?.Contract?.Check.Describe();

    /// <summary>The self-check of <paramref name="codeInfo"/>'s compiled contract: its verdict, and compiled time per opcode over the interpreter's.</summary>
    public static (string Verdict, double Ratio, int Samples)? SelfCheckOf(CodeInfo codeInfo) =>
        (IlEvmFrameRunner.CodeSlot(codeInfo) as IlCodeState)?.Contract?.Check is { } check
            ? (check.Verdict switch { IlSelfCheck.Compiled => "compiled", IlSelfCheck.Interpreted => "interpreted", IlSelfCheck.Probation => "probation", _ => "undecided" }, check.Ratio, check.InterpretedSamples)
            : null;
}
