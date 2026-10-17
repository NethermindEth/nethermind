// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Logging;

namespace Nethermind.IlEvm;

/// <summary>
/// JIT-compiles ahead, off the frames' threads, the methods the Adaptive rules' frame path and compiled code call, so the
/// first blocks after a start, and the first frames of a contract promoted between blocks, pay no JIT on block processing.
/// </summary>
/// <remarks>
/// <para>
/// A chunk's JIT compiles the chunk alone: what it calls - the helpers its IL calls, the opcode bodies it reaches through
/// their entry points, and what those call in turn - is compiled where it is first called, on whichever frame's thread
/// that is, tens of methods and milliseconds for a contract's first run. So once a chunk is compiled, the methods it calls
/// are, and what they call, a few levels down within Nethermind's own assemblies, read from their IL; and the plugin's own
/// methods on the frame path are, once, before the startup compile. Each method is compiled once a process.
/// </para>
/// <para>
/// A method compiled ahead is compiled as its first call would have it - at tier 0, or fully optimized where it asks to
/// be - and tiers up in the background as any other. Under the Legacy and measurement rules nothing is compiled ahead,
/// so they run as they did.
/// </para>
/// <para>
/// Tier 0 is not enough for the helpers compiled code calls out of line - arithmetic, comparisons, memory and calldata
/// words, the call of an embedded body - which a contract's parts call millions of times a second: at tier 0 they make
/// compiled code slower than the interpreter, and the runtime promotes nothing until its JIT has been quiet for a while,
/// seconds into a node's first calls. Calling them ahead on made-up operands would promote them, but profiled on those
/// operands, which costs compiled code a few percent for good; marking them optimized from the start would leave them
/// with no profile at all. So the list's RPC contracts get a copy laid out against ahead twins of the helpers, optimized
/// from their first call (<see cref="IlEvm.IlContract.Ahead"/>), which serves the first calls, while one compiled part in
/// <see cref="AheadOwnShare"/> runs the contract's own code, which tiers its helpers up on real calls; once the JIT is
/// quiet, <see cref="SwitchFromAhead"/> drops the copies.
/// </para>
/// </remarks>
internal static class IlWarmUp
{
    /// <summary>How many calls down from a chunk's own callees the methods called are compiled: opcode bodies reach the instructions and the gas rules they run several calls down.</summary>
    public const int MaxDepth = 8;

    /// <summary>How many calls down from the plugin's frame path the methods called are compiled: its own, and the core's few it reads.</summary>
    public const int FramePathDepth = 3;

    /// <summary>Each method met, with the fewest calls down it was met at: one met again higher up has more of what it calls compiled.</summary>
    private static readonly ConcurrentDictionary<MethodBase, int> _seen = new();
    private static readonly OpCode[] _oneByte = new OpCode[0x100], _twoByte = new OpCode[0x100];
    private static long _prepared, _failed;
    private static int _framePath;

    static IlWarmUp()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode)
                continue;
            ushort value = (ushort)opcode.Value;
            if (value < 0x100)
                _oneByte[value] = opcode;
            else if ((value & 0xFF00) == 0xFE00)
                _twoByte[value & 0xFF] = opcode;
        }
    }

    /// <summary>
    /// The longest the list's RPC contracts run their ahead copies after the first frame, in milliseconds:
    /// <c>NETHERMIND_ILEVM_AHEAD_MS</c>, 60,000 by default; 0 compiles no ahead copies.
    /// </summary>
    public static int AheadMaxMs { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_AHEAD_MS"), out int aheadMs) && aheadMs >= 0 ? aheadMs : 60_000;

    /// <summary>The least the ahead copies serve after the first frame, in milliseconds: the first calls' burst of JIT comes first.</summary>
    public const int AheadMinMs = 3_000;

    /// <summary>The window over which the JIT counts as quiet, in milliseconds, and the most methods it may compile in it.</summary>
    public const int AheadQuietMs = 1_000;

    public const int AheadQuietMethods = 16;

    /// <summary>
    /// One compiled part in this many, a power of two, runs a contract's own code while it has an ahead copy: its helpers
    /// get their first calls and their profile from real calls then, and are optimized by the time the copies go.
    /// </summary>
    public const int AheadOwnShare = 32;

    /// <summary><see cref="AheadOwnShare"/> less one, the mask a part's draw is taken with; tests run half the parts on either code.</summary>
    internal static int AheadOwnMask = AheadOwnShare - 1;

    /// <summary>How often the switch is considered, in milliseconds.</summary>
    private const int AheadTickMs = 250;

    private static readonly object _ahead = new();
    private static List<IlEvm.IlContract>? _aheadContracts;
    private static Timer? _aheadTimer;
    private static ILogger _aheadLogger;

    /// <summary>The JIT's compiled-method counts at the latest ticks, oldest first: the quiet window's.</summary>
    private static readonly Queue<(long At, long Methods)> _aheadTicks = new();

    private static int _aheadArmed, _aheadSwitched;
    private static long _aheadSwitchedAfterMs = -1;

    /// <summary>For tests: no timer switches the copies; the test does.</summary>
    internal static bool AheadManualForTests { get; set; }

    /// <summary>The contracts given ahead copies, and those that left them for their own code; and when, from the first frame, or -1 before.</summary>
    public static int AheadArmed => Volatile.Read(ref _aheadArmed);

    public static int AheadSwitched => Volatile.Read(ref _aheadSwitched);

    public static long AheadSwitchedAfterMs => Interlocked.Read(ref _aheadSwitchedAfterMs);

    /// <summary>
    /// Has <paramref name="contracts"/>, each with an ahead copy, run their copies until the switch: from
    /// <see cref="AheadMinMs"/> after the first frame once the JIT has compiled at most <see cref="AheadQuietMethods"/>
    /// methods over <see cref="AheadQuietMs"/>, and at <see cref="AheadMaxMs"/> whatever the JIT does.
    /// </summary>
    public static void ArmAhead(IEnumerable<IlEvm.IlContract> contracts, ILogger logger)
    {
        lock (_ahead)
        {
            _aheadContracts ??= [];
            foreach (IlEvm.IlContract contract in contracts)
            {
                if (contract.Ahead is not null)
                {
                    _aheadContracts.Add(contract);
                    Interlocked.Increment(ref _aheadArmed);
                }
            }

            _aheadLogger = logger;
            if (_aheadContracts.Count > 0 && _aheadTimer is null && !AheadManualForTests)
                _aheadTimer = new Timer(static _ => OnAheadTick(), null, AheadTickMs, AheadTickMs);
        }
    }

    private static void OnAheadTick()
    {
        long now = Environment.TickCount64;
        long methods = System.Runtime.JitInfo.GetCompiledMethodCount();
        long quietSince;
        lock (_ahead)
        {
            _aheadTicks.Enqueue((now, methods));
            while (_aheadTicks.Count > 1 && now - _aheadTicks.Peek().At > AheadQuietMs)
                _aheadTicks.Dequeue();
            (long at, long then) = _aheadTicks.Peek();
            quietSince = now - at >= AheadQuietMs - AheadTickMs && methods - then <= AheadQuietMethods ? at : -1;
        }

        long firstFrame = IlEvmSettings.FirstFrameAt;
        if (AheadDue(now, firstFrame, quietSince))
        {
            int switched = SwitchFromAhead(now - firstFrame);
            if (switched > 0 && _aheadLogger.IsInfo)
                _aheadLogger.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"IL-EVM: {switched} contracts of the RPC list left their ahead copies for their own code {(now - firstFrame) / 1000.0:F1} s after the first frame{(now - firstFrame >= AheadMaxMs ? ", at the cap" : ", the JIT quiet")}"));
        }
    }

    /// <summary>
    /// Whether the ahead copies are due to go at <paramref name="now"/>: a frame has run, and either
    /// <see cref="AheadMaxMs"/> have passed since the first, or <see cref="AheadMinMs"/> have and the JIT has been quiet
    /// since <paramref name="quietSince"/>, -1 for not quiet.
    /// </summary>
    internal static bool AheadDue(long now, long firstFrame, long quietSince) =>
        firstFrame != 0 && (now - firstFrame >= AheadMaxMs || (now - firstFrame >= AheadMinMs && quietSince >= 0));

    /// <summary>Drops every ahead copy: each contract runs its own code from its next part, its helpers tiering up on those calls; the contracts that switched.</summary>
    /// <param name="afterMs">When, from the first frame, for the exit line.</param>
    public static int SwitchFromAhead(long afterMs = 0)
    {
        List<IlEvm.IlContract>? contracts;
        lock (_ahead)
        {
            contracts = _aheadContracts;
            _aheadContracts = null;
            _aheadTimer?.Dispose();
            _aheadTimer = null;
            _aheadTicks.Clear();
        }

        if (contracts is null)
            return 0;
        foreach (IlEvm.IlContract contract in contracts)
            contract.Ahead = null;
        Interlocked.Add(ref _aheadSwitched, contracts.Count);
        Interlocked.CompareExchange(ref _aheadSwitchedAfterMs, Math.Max(0, afterMs), -1);
        return contracts.Count;
    }

    /// <summary>The ahead copies for the exit line.</summary>
    public static string DescribeAhead() => AheadArmed == 0
        ? "no ahead copies"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{AheadArmed} ahead copies, {AheadSwitched} switched to their own code{(AheadSwitchedAfterMs >= 0 ? $" {AheadSwitchedAfterMs / 1000.0:F1} s after the first frame" : "")}");

    /// <summary>Drops the ahead copies and forgets the counts, for tests.</summary>
    internal static void ResetAheadForTests()
    {
        SwitchFromAhead();
        Volatile.Write(ref _aheadArmed, 0);
        Volatile.Write(ref _aheadSwitched, 0);
        Interlocked.Exchange(ref _aheadSwitchedAfterMs, -1);
    }

    /// <summary>Methods compiled ahead so far, and those the runtime would not compile ahead.</summary>
    public static long Prepared => Interlocked.Read(ref _prepared);

    public static long Failed => Interlocked.Read(ref _failed);

    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _failures = new();

    /// <summary>The first methods the runtime would not compile ahead, with why, for the startup line.</summary>
    public static string DescribeFailures() => _failures.IsEmpty ? "" : " (" + string.Join("; ", _failures) + (Failed > _failures.Count ? "; ..." : "") + ")";

    private static void RecordFailure(MethodBase method, Exception exception)
    {
        if (_failures.Count < 8)
            _failures.Enqueue($"{method.DeclaringType?.Name}.{method.Name}: {exception.GetType().Name}: {exception.Message.Split('\n')[0]}");
    }

    /// <summary>Whether the plugin's frame path was compiled ahead.</summary>
    public static bool FramePathPrepared => Volatile.Read(ref _framePath) == 2;

    /// <summary>Compiles <paramref name="methods"/> ahead, with what they call to <see cref="MaxDepth"/>; each method once a process.</summary>
    /// <remarks>
    /// A method met before only deep down, where what it calls was left, is walked again when met higher up: the methods
    /// it calls are then compiled too, whichever walk met it first.
    /// </remarks>
    public static void Prepare(IEnumerable<MethodBase>? methods) => Prepare(methods, MaxDepth);

    /// <summary>As <see cref="Prepare(IEnumerable{MethodBase})"/>, to <paramref name="maxDepth"/> calls down.</summary>
    private static void Prepare(IEnumerable<MethodBase>? methods, int maxDepth)
    {
        if (methods is null)
            return;
        Queue<(MethodBase Method, int Depth)> pending = new();
        foreach (MethodBase method in methods)
            pending.Enqueue((method, 0));
        while (pending.TryDequeue(out (MethodBase Method, int Depth) next))
        {
            bool first = true;
            while (true)
            {
                if (_seen.TryAdd(next.Method, next.Depth))
                    break;
                first = false;
                if (!_seen.TryGetValue(next.Method, out int seenAt) || seenAt <= next.Depth)
                    goto Skip;
                if (_seen.TryUpdate(next.Method, next.Depth, seenAt))
                    break;
            }

            // Compiled once; walked again, higher up, for what it calls.
            if ((first ? !PrepareOne(next.Method) : next.Method.IsAbstract) || next.Depth >= maxDepth || !IsOurs(next.Method))
                continue;
            foreach (MethodBase callee in CalleesOf(next.Method))
                pending.Enqueue((callee, next.Depth + 1));
            Skip:;
        }
    }

    /// <summary>
    /// Compiles ahead, once, every method of the plugin's types a frame part may reach under the Adaptive rules: the
    /// runner, the gate, the lookup, the checks, live profiling's counts and the contracts' run, with what they call.
    /// </summary>
    /// <returns>
    /// What of the core's side it could not find, for the log, or null. The core's hook path is found by name, best
    /// effort: it is not part of the hook's API, and a core that renames it leaves that path to its first frames.
    /// </returns>
    public static string? PrepareFramePath()
    {
        if (Interlocked.CompareExchange(ref _framePath, 1, 0) != 0)
            return null;
        List<MethodBase> methods = [];
        foreach (Type type in (Type[])[typeof(IlEvmFrameRunner), typeof(IlEvmLegacyFrameRunner<OffFlag>), typeof(IlEvmLegacyFrameRunner<OnFlag>), typeof(IlSurfaceCounts), typeof(IlThread), typeof(IlRuntime), typeof(IlCheck), typeof(IlCheckClock),
                     typeof(IlLive), typeof(IlVisitStats), typeof(IlGate), typeof(IlCodeProfile), typeof(IlEvm.IlContract), typeof(IlEvm.IlChunk), typeof(IlEvm)])
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                methods.Add(method);
        }

        // The core's hook into the runner, which a machine with no runner never calls, for both of its flavours.
        string? missing = "the core's RunFrameCodeOnRunner was not found, so its hook path compiles at its first frames";
        try
        {
            foreach (MethodInfo method in typeof(VirtualMachine<EthereumGasPolicy>).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name == "RunFrameCodeOnRunner" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1)
                {
                    methods.Add(method.MakeGenericMethod(typeof(OffFlag)));
                    methods.Add(method.MakeGenericMethod(typeof(OnFlag)));
                    missing = null;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Another shape: as not found, which the caller logs.
            missing = $"the core's RunFrameCodeOnRunner could not be instantiated ({exception.GetType().Name}: {exception.Message}), so its hook path compiles at its first frames";
        }

        Prepare(methods, FramePathDepth);
        // The comparer the runtime's own generic code makes over the plugin's key, whose instantiation nothing above
        // reaches: a resolution's first lookup among the profiles that finds one.
        EqualityComparer<IlProfileKey>.Default.Equals(default, default);
        Volatile.Write(ref _framePath, 2);
        return missing;
    }

    /// <summary>Compiles one method ahead; false when it has no body of its own to compile, or the runtime declined.</summary>
    private static bool PrepareOne(MethodBase method)
    {
        if (method.IsAbstract || method.ContainsGenericParameters || method.DeclaringType is { ContainsGenericParameters: true } ||
            (method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0 ||
            (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            return false;
        try
        {
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
            catch (ArgumentException) when (method.IsGenericMethod || method.DeclaringType is { IsGenericType: true })
            {
                // Shared generic code is compiled ahead with its exact instantiation: the declaring type's arguments, then the method's.
                RuntimeHelpers.PrepareMethod(method.MethodHandle, Handles([.. method.DeclaringType!.GetGenericArguments(), .. method.IsGenericMethod ? method.GetGenericArguments() : []]));
            }

            Interlocked.Increment(ref _prepared);
            return true;
        }
        catch (Exception exception)
        {
            // A method the runtime does not compile ahead, as some of its own: compiled at its first call, as before.
            Interlocked.Increment(ref _failed);
            RecordFailure(method, exception);
            return false;
        }
    }

    private static RuntimeTypeHandle[] Handles(Type[] types)
    {
        RuntimeTypeHandle[] handles = new RuntimeTypeHandle[types.Length];
        for (int i = 0; i < types.Length; i++)
            handles[i] = types[i].TypeHandle;
        return handles;
    }

    /// <summary>Whether the method is Nethermind's, whose callees are read from its IL; the runtime's own are precompiled.</summary>
    private static bool IsOurs(MethodBase method) => method.Module.Assembly.GetName().Name?.StartsWith("Nethermind", StringComparison.Ordinal) == true;

    /// <summary>The methods <paramref name="method"/>'s IL calls, constructs or takes the address of, resolved in its generic context; a constrained call goes to the type's own implementation.</summary>
    internal static List<MethodBase> CalleesOf(MethodBase method)
    {
        List<MethodBase> callees = [];
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            return callees;
        }

        if (il is null)
            return callees;
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        Type? constrained = null;
        for (int at = 0; at < il.Length;)
        {
            OpCode opcode;
            if (il[at] == 0xFE)
            {
                if (at + 1 >= il.Length)
                    break;
                opcode = _twoByte[il[at + 1]];
                at += 2;
            }
            else
            {
                opcode = _oneByte[il[at]];
                at++;
            }

            int operand = at;
            at += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => at + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, at) : il.Length,
                _ => 4,
            };
            if (at > il.Length)
                break;

            try
            {
                if (opcode == OpCodes.Constrained)
                {
                    constrained = method.Module.ResolveType(BitConverter.ToInt32(il, operand), typeArguments, methodArguments);
                    continue;
                }

                if (opcode.OperandType != OperandType.InlineMethod)
                    continue;
                MethodBase? callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, operand), typeArguments, methodArguments);
                if (callee is MethodInfo target && constrained is not null && target.DeclaringType is { IsInterface: true } contract)
                {
                    // The type's own implementation, and the interface's default body, which serves where the type has none.
                    if (Implementation(constrained, contract, target) is { } implementation)
                        callees.Add(implementation);
                }

                constrained = null;
                if (callee is not null)
                    callees.Add(callee);
            }
            catch (Exception)
            {
                // A token this context cannot resolve: its method is compiled at its first call, as before.
                constrained = null;
            }
        }

        return callees;
    }

    /// <summary>
    /// <paramref name="type"/>'s implementation of <paramref name="method"/> of <paramref name="contract"/>, or null for
    /// none of its own, as where the interface's default body serves: by the interface map, else, where the runtime maps no
    /// static members, by the name a method implementing it takes, implicitly or explicitly, and its parameters.
    /// </summary>
    private static MethodInfo? Implementation(Type type, Type contract, MethodInfo method)
    {
        // A generic method's implementation comes as its definition: instantiated as the call was.
        Type[] arguments = method.IsGenericMethod ? method.GetGenericArguments() : [];
        MethodInfo? Instantiated(MethodInfo found) =>
            found.IsGenericMethodDefinition ? (found.GetGenericArguments().Length == arguments.Length ? found.MakeGenericMethod(arguments) : null) : found;
        try
        {
            InterfaceMapping map = type.GetInterfaceMap(contract);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].MetadataToken == method.MetadataToken && map.InterfaceMethods[i].Module == method.Module)
                    return Instantiated(map.TargetMethods[i]);
            }
        }
        catch (Exception)
        {
            // Mapped by name below.
        }

        int parameters = method.GetParameters().Length;
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly | (method.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
        foreach (MethodInfo candidate in type.GetMethods(flags))
        {
            if ((candidate.Name == method.Name || candidate.Name.EndsWith("." + method.Name, StringComparison.Ordinal)) &&
                candidate.GetParameters().Length == parameters && candidate.IsGenericMethodDefinition == method.IsGenericMethod)
            {
                try
                {
                    return Instantiated(candidate);
                }
                catch (ArgumentException)
                {
                    // Arguments the candidate's constraints refuse: not the implementation.
                }
            }
        }

        return null;
    }

}
