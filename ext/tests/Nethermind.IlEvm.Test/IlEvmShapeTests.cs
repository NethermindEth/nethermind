// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Reflection.Emit;
using System.Threading;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The code shapes of the EVM compute benchmarks, at a few hundred bytes and at the maximum contract size: one opcode
/// pattern repeated through a loop until gas runs out, long straight runs, long dispatchers.
/// </summary>
/// <remarks>
/// Compiling a large contract as one method leaves the JIT to compile it without optimizations, so code that is fast at
/// a few hundred bytes is slow at 24 KB. These shapes must compile at both sizes, match the interpreter, and leave no
/// compiled method to the JIT's minimal optimizations.
/// </remarks>
public partial class IlEvmDifferentialTests
{
    private const int MaxCodeSize = 24_576;

    /// <summary>A benchmark program: its code, the calldata it runs with and the gas of one run.</summary>
    public sealed record Shape(string Name, string Size, byte[] Code, byte[] Input, ulong Gas)
    {
        public override string ToString() => $"{Name}/{Size}";
    }

    private sealed class Bytecode
    {
        private readonly List<byte> _code = [];

        public int Length => _code.Count;

        public Bytecode Op(params Instruction[] instructions)
        {
            foreach (Instruction instruction in instructions)
                _code.Add((byte)instruction);
            return this;
        }

        /// <summary>PUSH<paramref name="size"/> of <paramref name="value"/>, big-endian in its low bytes; PUSH0 for size 0.</summary>
        public Bytecode Push(int size, ulong value)
        {
            _code.Add((byte)(size == 0 ? Instruction.PUSH0 : Instruction.PUSH1 + (byte)(size - 1)));
            for (int i = size - 1; i >= 0; i--)
                _code.Add(i < 8 ? (byte)(value >> (8 * i)) : (byte)0);
            return this;
        }

        /// <summary>PUSH32 of a word with every byte set, different for each <paramref name="seed"/>.</summary>
        public Bytecode PushWord(int seed)
        {
            _code.Add((byte)Instruction.PUSH32);
            for (int i = 0; i < 32; i++)
                _code.Add((byte)(seed * 61 + i * 17 + 0x81));
            return this;
        }

        public Bytecode Append(Bytecode other)
        {
            _code.AddRange(other._code);
            return this;
        }

        public byte[] ToArray() => _code.ToArray();
    }

    /// <summary>
    /// <paramref name="setup"/>, then a loop: a JUMPDEST, the body repeated to fill <paramref name="target"/> bytes, and a
    /// jump back. It runs until gas runs out, as the benchmarks' attack contracts do.
    /// </summary>
    /// <param name="body">The body at a code offset, for bodies that jump within themselves.</param>
    private static Shape Loop(string name, string size, int target, Bytecode setup, Func<int, Bytecode> body, byte[]? input = null, ulong gas = 1_000_000)
    {
        Bytecode code = new Bytecode().Append(setup);
        int loop = code.Length;
        code.Op(Instruction.JUMPDEST);
        int bodyLength = body(0).Length;
        int count = Math.Max(1, (target - code.Length - 4) / bodyLength);
        for (int i = 0; i < count; i++)
            code.Append(body(code.Length));
        code.Push(2, (ulong)loop).Op(Instruction.JUMP);
        return new Shape(name, size, code.ToArray(), input ?? [], gas);
    }

    private static Shape Loop(string name, string size, int target, Bytecode setup, Bytecode body, byte[]? input = null, ulong gas = 1_000_000) =>
        Loop(name, size, target, setup, _ => body, input, gas);

    /// <summary>Two rounds of modular arithmetic on a pair of words and a memory round trip, net zero on the stack, as unrolled verifier code runs.</summary>
    private static Bytecode VerifierRound(int round)
    {
        Bytecode code = new();
        // [a, b] -> [b, (a*b) mod P] -> [(a*b) mod P, (b + (a*b)) mod P]
        code.PushWord(7).Op(Instruction.DUP2, Instruction.DUP4, Instruction.MULMOD, Instruction.SWAP2, Instruction.POP);
        code.PushWord(7).Op(Instruction.DUP2, Instruction.DUP4, Instruction.ADDMOD, Instruction.SWAP2, Instruction.POP);
        int offset = (round % 32) * 32;
        code.Op(Instruction.DUP1).Push(2, (ulong)offset).Op(Instruction.MSTORE);
        code.Push(2, (ulong)offset).Op(Instruction.MLOAD, Instruction.XOR);
        return code;
    }

    private static Shape Straight(string name, string size, int target, Bytecode setup, Func<int, Bytecode> round, ulong gas = 30_000_000)
    {
        Bytecode code = new Bytecode().Append(setup);
        for (int i = 0; code.Length + round(i).Length < target; i++)
            code.Append(round(i));
        code.Op(Instruction.STOP);
        return new Shape(name, size, code.ToArray(), [], gas);
    }

    /// <summary>A selector dispatcher of <paramref name="functions"/> external functions, compared one after another, called for its last.</summary>
    private static Shape Dispatcher(string size, int functions, bool last)
    {
        Bytecode code = new Bytecode().Push(1, 0).Op(Instruction.CALLDATALOAD).Push(1, 0xe0).Op(Instruction.SHR);
        int header = code.Length;
        const int Compare = 11;
        int bodies = header + functions * Compare + 3;
        const int Body = 12;
        for (int i = 0; i < functions; i++)
            code.Op(Instruction.DUP1).Push(4, Selector(i)).Op(Instruction.EQ).Push(2, (ulong)(bodies + i * Body)).Op(Instruction.JUMPI);
        code.Push(0, 0).Push(0, 0).Op(Instruction.REVERT);
        for (int i = 0; i < functions; i++)
            code.Op(Instruction.JUMPDEST).Push(2, (ulong)i).Push(1, 0).Op(Instruction.MSTORE).Push(1, 32).Push(1, 0).Op(Instruction.RETURN);

        uint selector = Selector(last ? functions - 1 : 0);
        byte[] input = [(byte)(selector >> 24), (byte)(selector >> 16), (byte)(selector >> 8), (byte)selector];
        return new Shape(last ? "dispatch-last" : "dispatch-first", size, code.ToArray(), input, 1_000_000);

        static uint Selector(int i) => (uint)(0x1234_5678 + i * 0x0101_0103);
    }

    public static IEnumerable<Shape> Shapes()
    {
        foreach ((string size, int target) in new[] { ("small", 256), ("max", MaxCodeSize) })
        {
            // Binary operations as the compute benchmarks run them: two words kept on the stack, copied, combined, dropped.
            foreach (Instruction op in new[]
                     {
                         Instruction.ADD, Instruction.MUL, Instruction.SUB, Instruction.DIV, Instruction.SDIV, Instruction.MOD,
                         Instruction.SMOD, Instruction.EXP, Instruction.SIGNEXTEND, Instruction.LT, Instruction.GT, Instruction.SLT,
                         Instruction.SGT, Instruction.EQ, Instruction.AND, Instruction.OR, Instruction.XOR, Instruction.BYTE,
                         Instruction.SHL, Instruction.SHR, Instruction.SAR,
                     })
            {
                Bytecode setup = new Bytecode().PushWord(2);
                if (op is Instruction.SIGNEXTEND or Instruction.BYTE or Instruction.SHL or Instruction.SHR or Instruction.SAR)
                    setup.Push(1, 7);
                else
                    setup.PushWord(1);
                yield return Loop(op.ToString(), size, target, setup, new Bytecode().Op(Instruction.DUP2, Instruction.DUP2, op, Instruction.POP));
            }

            foreach (Instruction op in new[] { Instruction.ADDMOD, Instruction.MULMOD })
                yield return Loop(op.ToString(), size, target, new Bytecode().PushWord(3).PushWord(2).PushWord(1),
                    new Bytecode().Op(Instruction.DUP3, Instruction.DUP3, Instruction.DUP3, op, Instruction.POP));

            // Unary operations chained on one word, and on a copy that is dropped.
            foreach (Instruction op in new[] { Instruction.ISZERO, Instruction.NOT, Instruction.CLZ })
            {
                yield return Loop($"{op}-chain", size, target, new Bytecode().PushWord(1), new Bytecode().Op(op));
                yield return Loop($"{op}-copy", size, target, new Bytecode().PushWord(1), new Bytecode().Op(Instruction.DUP1, op, Instruction.POP));
            }

            // Stack opcodes.
            foreach (int width in new[] { 0, 1, 16, 32 })
                yield return Loop($"PUSH{width}-POP", size, target, new Bytecode(), new Bytecode().Push(width, 0xa5a5_a5a5_a5a5_a5a5).Op(Instruction.POP));
            {
                int depth = size == "max" ? 700 : 3;
                Bytecode fill = new();
                for (int i = 0; i < depth; i++) fill.PushWord(i);
                for (int i = 0; i < depth; i++) fill.Op(Instruction.POP);
                yield return Loop("stack-fill", size, target, new Bytecode(), fill);
            }
            Bytecode sixteen = new();
            for (int i = 0; i < 17; i++) sixteen.Push(1, (ulong)(i + 1));
            foreach (int n in new[] { 1, 8, 16 })
            {
                yield return Loop($"DUP{n}", size, target, sixteen, new Bytecode().Op(Instruction.DUP1 + (byte)(n - 1), Instruction.POP));
                yield return Loop($"SWAP{n}", size, target, sixteen, new Bytecode().Op(Instruction.SWAP1 + (byte)(n - 1)));
            }

            // Control flow.
            yield return Loop("JUMPDEST", size, target, new Bytecode(), new Bytecode().Op(Instruction.JUMPDEST));
            yield return Loop("JUMP", size, target, new Bytecode(), at => new Bytecode().Push(2, (ulong)(at + 4)).Op(Instruction.JUMP, Instruction.JUMPDEST));
            yield return Loop("JUMPI-taken", size, target, new Bytecode(), at => new Bytecode().Push(1, 1).Push(2, (ulong)(at + 6)).Op(Instruction.JUMPI, Instruction.JUMPDEST));
            yield return Loop("JUMPI-not", size, target, new Bytecode(), new Bytecode().Push(0, 0).Push(2, 0).Op(Instruction.JUMPI));

            // Memory.
            yield return Loop("MLOAD", size, target, new Bytecode(), new Bytecode().Push(1, 0).Op(Instruction.MLOAD, Instruction.POP));
            yield return Loop("MSTORE-const", size, target, new Bytecode(), new Bytecode().Push(1, 0x2a).Push(1, 0).Op(Instruction.MSTORE));
            yield return Loop("MSTORE-word", size, target, new Bytecode().PushWord(1), new Bytecode().Op(Instruction.DUP1).Push(1, 32).Op(Instruction.MSTORE));
            yield return Loop("MSTORE8", size, target, new Bytecode(), new Bytecode().Push(1, 0x2a).Push(1, 0).Op(Instruction.MSTORE8));
            yield return Loop("MCOPY", size, target, new Bytecode(), new Bytecode().Push(1, 32).Push(1, 0).Push(1, 32).Op(Instruction.MCOPY));
            yield return Loop("MSIZE", size, target, new Bytecode().Push(1, 0).Op(Instruction.MLOAD, Instruction.POP), new Bytecode().Op(Instruction.MSIZE, Instruction.POP));

            // Environment and hashing.
            foreach (Instruction op in new[]
                     {
                         Instruction.ADDRESS, Instruction.CALLER, Instruction.CALLVALUE, Instruction.CALLDATASIZE, Instruction.CODESIZE,
                         Instruction.GAS, Instruction.PC, Instruction.RETURNDATASIZE, Instruction.TIMESTAMP, Instruction.NUMBER,
                         Instruction.CHAINID, Instruction.BASEFEE, Instruction.SELFBALANCE,
                     })
                yield return Loop(op.ToString(), size, target, new Bytecode(), new Bytecode().Op(op, Instruction.POP));
            byte[] calldata = Enumerable.Range(1, 64).Select(static i => (byte)i).ToArray();
            yield return Loop("CALLDATALOAD", size, target, new Bytecode(), new Bytecode().Push(1, 0).Op(Instruction.CALLDATALOAD, Instruction.POP), calldata);
            yield return Loop("CALLDATACOPY", size, target, new Bytecode(), new Bytecode().Push(1, 32).Push(1, 0).Push(1, 0).Op(Instruction.CALLDATACOPY), calldata);
            yield return Loop("KECCAK256", size, target, new Bytecode(), new Bytecode().Push(1, 32).Push(1, 0).Op(Instruction.KECCAK256, Instruction.POP));
            yield return Loop("TLOAD", size, target, new Bytecode(), new Bytecode().Push(1, 0).Op(Instruction.TLOAD, Instruction.POP));
            yield return Loop("TSTORE", size, target, new Bytecode(), new Bytecode().Push(1, 1).Push(1, 0).Op(Instruction.TSTORE));
            yield return Loop("SLOAD", size, target, new Bytecode(), new Bytecode().Push(1, 0).Op(Instruction.SLOAD, Instruction.POP));

            // Unrolled modular arithmetic with memory, once through and in a loop; dispatchers.
            Bytecode pair = new Bytecode().PushWord(1).PushWord(2);
            yield return Straight("verifier-straight", size, target, pair, VerifierRound);
            yield return Loop("verifier-loop", size, target, pair, at => VerifierRound(at / 64), gas: 3_000_000);
            int functions = size == "max" ? 1_000 : 8;
            yield return Dispatcher(size, functions, last: true);
            yield return Dispatcher(size, functions, last: false);
        }
    }

    /// <summary>Every shape compiles at both sizes and runs exactly as on the interpreter, at full gas and at a cut that faults midway.</summary>
    [TestCaseSource(nameof(Shapes))]
    public void Benchmark_shapes_compile_and_match_the_interpreter(Shape shape)
    {
        CodeInfo codeInfo = new(shape.Code);
        foreach (ulong gas in new ulong[] { shape.Gas, shape.Gas / 3 + 7, 60_000 })
        {
            Outcome expected = Run(codeInfo, shape.Input, gas, compiledCode: false);
            Outcome actual = Run(codeInfo, shape.Input, gas, compiledCode: true);
            Assert.That(actual, Is.EqualTo(expected), $"{shape} at gas {gas}");
        }

        Assert.That(IlEvm.FailureReason(codeInfo), Is.Null, shape.ToString());
        Assert.That(IlEvm.IsCompiled(codeInfo), Is.True, shape.ToString());
    }

    /// <summary>No compiled method of any shape, at any size, is left to the JIT's minimal optimizations.</summary>
    /// <remarks>
    /// The runtime reports each method's optimization tier as it loads it; a method the JIT switched to minimal
    /// optimizations, for its size, reports MinOptJitted. A probe method past the JIT's limits checks that the report
    /// can be read at all.
    /// </remarks>
    [Test]
    public void Benchmark_shapes_compile_to_optimized_code()
    {
        using JitTierListener listener = new();
        bool savedEager = IlEvmSettings.EagerChunks;
        IlEvmSettings.EagerChunks = true;
        try
        {
            RunProbe("evm_probe_large", 26_000);
            RunProbe("evm_probe_small", 10);
            // Unreachable bytes after the code make each one new to the compiled-contract cache, so it is compiled here.
            foreach (Shape shape in Shapes())
                Run(new CodeInfo(shape.Code.Concat(new byte[] { (byte)Instruction.INVALID, 0x5a, 0x17 }).ToArray()), shape.Input, 60_000, compiledCode: true);
        }
        finally
        {
            IlEvmSettings.EagerChunks = savedEager;
        }

        Assert.That(listener.WaitFor("evm_probe_small"), Is.True, "the runtime's method load events did not arrive");
        Assert.That(listener.WaitFor("evm_probe_large"), Is.True, "the runtime's method load events did not arrive");
        Assert.That(listener.TierOf("evm_probe_large"), Is.EqualTo(JitTierListener.MinOptJitted), "a method past the JIT's limits should report minimal optimizations");
        Assert.That(listener.TierOf("evm_probe_small"), Is.EqualTo(JitTierListener.Optimized));

        // Give the dispatch thread time to deliver the chunks' events too.
        Thread.Sleep(2_000);
        (string Method, int Tier)[] chunks = listener.Methods.Where(static m => !m.Method.StartsWith("evm_probe", StringComparison.Ordinal)).ToArray();
        TestContext.Out.WriteLine($"{chunks.Length} compiled methods: {string.Join(", ", chunks.GroupBy(static m => m.Tier).Select(static g => $"{g.Count()} at tier {g.Key}"))}");
        Assert.That(chunks.Length, Is.GreaterThan(Shapes().Count()));
        Assert.That(chunks.Where(static m => m.Tier != JitTierListener.Optimized).Select(static m => $"{m.Method} tier {m.Tier}"), Is.Empty);
    }

    /// <summary>A dynamic method of <paramref name="pairs"/> push-and-drop pairs, compiled by calling it once.</summary>
    private static void RunProbe(string name, int pairs)
    {
        DynamicMethod method = new(name, typeof(int), Type.EmptyTypes, typeof(IlEvmDifferentialTests).Module, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        for (int i = 0; i < pairs; i++)
        {
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Pop);
        }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
    }

    /// <summary>Times each shape on both machines in alternating rounds, with what its compilation cost.</summary>
    [Test, Explicit("Timing")]
    public void Benchmark_shapes_timing()
    {
        Shape[] shapes = Shapes().ToArray();
        // Performance cores only, at high priority: a hybrid laptop part otherwise moves the thread between core types
        // and clocks, which swamps the differences timed here. A mask of two or more cores leaves the runtime's
        // background threads a core of their own.
        if (long.TryParse(Environment.GetEnvironmentVariable("ILEVM_AFFINITY"), System.Globalization.NumberStyles.HexNumber, null, out long affinity) && affinity > 0)
        {
            using Process self = Process.GetCurrentProcess();
            self.ProcessorAffinity = (nint)affinity;
            self.PriorityClass = ProcessPriorityClass.High;
        }
        string? only = Environment.GetEnvironmentVariable("ILEVM_SHAPES");
        if (only is not null)
            shapes = shapes.Where(s => only.Split(',').Any(o => s.ToString().Contains(o, StringComparison.OrdinalIgnoreCase))).ToArray();

        // Every shape on both machines first, so the interpreter and the helpers are tiered up before any timing.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (Shape shape in shapes)
            {
                CodeInfo warm = new(shape.Code);
                Run(warm, shape.Input, 60_000, compiledCode: false);
                Run(warm, shape.Input, 60_000, compiledCode: true);
            }
        }

        int Rounds = int.TryParse(Environment.GetEnvironmentVariable("ILEVM_ROUNDS"), out int rounds) && rounds > 0 ? rounds : 7;
        TestContext.Progress.WriteLine($"{"shape",-22} {"size",-6} {"bytes",6} {"interpreter",12} {"compiled",12} {"speedup",8} {"chunks",6} {"IL KiB",7} {"JIT ms",7}");
        Dictionary<string, List<double>> bySize = [];
        Dictionary<string, List<double>> effectiveBySize = [];
        bool savedSelfCheck = IlEvmSettings.SelfCheck;
        foreach (Shape shape in shapes)
        {
            CodeInfo codeInfo = new(shape.Code);
            long compileStart = Stopwatch.GetTimestamp();
            Run(codeInfo, shape.Input, shape.Gas, compiledCode: true);
            double firstRunMs = Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds;
            for (int i = 0; i < 3; i++)
            {
                Run(codeInfo, shape.Input, shape.Gas, compiledCode: false);
                Run(codeInfo, shape.Input, shape.Gas, compiledCode: true);
            }

            // The self-check's verdict, as production would reach it, then compiled code timed regardless of it.
            IlEvmSettings.SelfCheck = true;
            int savedRecheck = IlEvmSettings.SelfCheckRecheckMs;
            int savedDelay = IlEvmSettings.SelfCheckDelayMs;
            IlEvmSettings.SelfCheckRecheckMs = 2_000;
            // The shape was compiled and warmed above; its helpers have run since.
            IlEvmSettings.SelfCheckDelayMs = 0;
            long checkStart = Stopwatch.GetTimestamp();
            while (IlEvm.SelfCheckOf(codeInfo) is { Verdict: "undecided" or "probation" } && Stopwatch.GetElapsedTime(checkStart).TotalSeconds < 20)
                Run(codeInfo, shape.Input, shape.Gas, compiledCode: true);
            IlEvmSettings.SelfCheckRecheckMs = savedRecheck;
            IlEvmSettings.SelfCheckDelayMs = savedDelay;
            (string Verdict, double Ratio, int Samples) check = IlEvm.SelfCheckOf(codeInfo) ?? ("none", 0, 0);
            IlEvmSettings.SelfCheck = false;
            if (Environment.GetEnvironmentVariable("ILEVM_SELF_CHECK_SAMPLES") == "1")
                TestContext.Progress.WriteLine($"    {shape} self-check samples ns/op: {IlEvm.DescribeSelfCheck(codeInfo)}");

            long single = Stopwatch.GetTimestamp();
            Run(codeInfo, shape.Input, shape.Gas, compiledCode: false);
            single = Math.Max(1, Stopwatch.GetTimestamp() - single);
            int perRound = (int)Math.Clamp(Stopwatch.Frequency / 20 / single, 1, 20_000);

            long interpreter = long.MaxValue, compiled = long.MaxValue;
            for (int round = 0; round < Rounds; round++)
            {
                foreach (bool compiledCode in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < perRound; i++)
                        Run(codeInfo, shape.Input, shape.Gas, compiledCode);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    if (compiledCode) compiled = Math.Min(compiled, elapsed);
                    else interpreter = Math.Min(interpreter, elapsed);
                }
            }

            double speedup = (double)interpreter / compiled;
            // What production runs: compiled code, or the interpreter where the self-check gave the contract back.
            double effective = check.Verdict == "interpreted" ? 1.0 : speedup;
            (bySize.TryGetValue(shape.Size, out List<double>? list) ? list : bySize[shape.Size] = []).Add(speedup);
            (effectiveBySize.TryGetValue(shape.Size, out List<double>? effectiveList) ? effectiveList : effectiveBySize[shape.Size] = []).Add(effective);
            (int IlBytes, TimeSpan JitTime, int Chunks) stats = IlEvm.CompileStats(codeInfo) ?? default;
            TestContext.Progress.WriteLine(
                $"{shape.Name,-22} {shape.Size,-6} {shape.Code.Length,6} {Us(interpreter, perRound),10:F1}us {Us(compiled, perRound),10:F1}us {speedup,7:F2}x {stats.Chunks,6} {stats.IlBytes / 1024.0,7:F1} {stats.JitTime.TotalMilliseconds,7:F1}  first run {firstRunMs:F1} ms  self-check {check.Verdict} {check.Ratio:F2} ({check.Samples}){(IlEvm.FailureReason(codeInfo) is { } failure ? "  NOT COMPILED: " + failure : "")}");
        }

        IlEvmSettings.SelfCheck = savedSelfCheck;
        foreach ((string size, List<double> speedups) in bySize)
            TestContext.Progress.WriteLine($"{size}: {speedups.Count} shapes, geometric-mean speedup x{Math.Exp(speedups.Average(Math.Log)):F2}, min x{speedups.Min():F2}, max x{speedups.Max():F2}");
        foreach ((string size, List<double> speedups) in effectiveBySize)
            TestContext.Progress.WriteLine($"{size} with the self-check: geometric-mean speedup x{Math.Exp(speedups.Average(Math.Log)):F2}, min x{speedups.Min():F2}, {speedups.Count(static x => x == 1.0)} given back to the interpreter");

        static double Us(long ticks, int perRound) => ticks * 1e6 / Stopwatch.Frequency / perRound;
    }

    /// <summary>What the IL of each shape implements, with <c>NETHERMIND_ILEVM_DIAG=1</c>; every chunk compiled.</summary>
    [Test, Explicit("Diagnostics")]
    public void Benchmark_shapes_il()
    {
        if (!IlEvmDiagnostics.Enabled)
            Assert.Ignore("needs NETHERMIND_ILEVM_DIAG=1");
        string? only = Environment.GetEnvironmentVariable("ILEVM_SHAPES");
        bool savedEager = IlEvmSettings.EagerChunks;
        IlEvmSettings.EagerChunks = true;
        try
        {
            foreach (Shape shape in Shapes())
            {
                if (only is not null && !only.Split(',').Any(o => shape.ToString().Contains(o, StringComparison.OrdinalIgnoreCase)))
                    continue;
                IlEvmDiagnostics.IlByCategory.Clear();
                CodeInfo codeInfo = new(shape.Code.Concat(new byte[] { (byte)Instruction.INVALID, 0x5a, 0x18 }).ToArray());
                Run(codeInfo, shape.Input, 60_000, compiledCode: true);
                (int IlBytes, TimeSpan JitTime, int Chunks) stats = IlEvm.CompileStats(codeInfo) ?? default;
                TestContext.Progress.WriteLine($"{shape}: {shape.Code.Length} B, {stats.Chunks} chunks, {stats.IlBytes / 1024.0:F1} KiB IL, JIT {stats.JitTime.TotalMilliseconds:F1} ms");
                foreach (string line in IlEvmDiagnostics.DescribeIl(10))
                    TestContext.Progress.WriteLine("    " + line);
            }
        }
        finally
        {
            IlEvmSettings.EagerChunks = savedEager;
        }
    }

    /// <summary>The optimization tier the runtime reports for each dynamic method it loads.</summary>
    private sealed class JitTierListener : EventListener
    {
        /// <summary>The tiers of <c>MethodFlags</c> bits 7 to 9.</summary>
        public const int MinOptJitted = 1, Optimized = 2;

        private readonly object _lock = new();
        private readonly Dictionary<string, int> _tiers = [];

        public IEnumerable<(string Method, int Tier)> Methods
        {
            get
            {
                lock (_lock) return _tiers.Select(static kv => (kv.Key, kv.Value)).ToArray();
            }
        }

        public int TierOf(string method)
        {
            lock (_lock) return _tiers.TryGetValue(method, out int tier) ? tier : -1;
        }

        public bool WaitFor(string method)
        {
            for (int i = 0; i < 100; i++)
            {
                if (TierOf(method) >= 0) return true;
                Thread.Sleep(50);
            }

            return false;
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10); // JitKeyword
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal))
                return;
            ReadOnlyCollection<string>? names = e.PayloadNames;
            if (names is null || e.Payload is null)
                return;
            int nameIndex = names.IndexOf("MethodName");
            int flagsIndex = names.IndexOf("MethodFlags");
            if (nameIndex < 0 || flagsIndex < 0 || e.Payload[nameIndex] is not string method || !method.StartsWith("evm_", StringComparison.Ordinal))
                return;
            int tier = (int)((Convert.ToUInt32(e.Payload[flagsIndex]) >> 7) & 0x7);
            lock (_lock) _tiers[method] = tier;
        }
    }
}
