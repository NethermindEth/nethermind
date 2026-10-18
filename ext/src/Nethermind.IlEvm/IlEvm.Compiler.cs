// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    internal sealed class IlInstruction
    {
        public int Pc;
        public byte Opcode;
        public int Size;
        public IlOp Op = null!;
        public ulong U0, U1, U2, U3;
        /// <summary>Static gas of the instructions after this one in its segment, which the segment has already charged.</summary>
        public ulong GasAfter;
        /// <summary>The instruction ends its segment, so the one after it starts a unit a body may continue at.</summary>
        public bool EndsSegment;
        /// <summary>A body that may run the next instruction itself, and then continues past it.</summary>
        public bool MayFuseNext;
    }

    internal sealed class IlUnit
    {
        public int StartPc;
        public bool IsSegment;
        public bool IsJumpDest;
        public readonly List<IlInstruction> Instructions = [];
        public ulong Gas;
        public int MinHeight;
        public int MaxPeak;
        public int EndDelta;
        public int FirstTableIndex;
        /// <summary>For a segment, roughly the IL instructions it compiles to; see <see cref="IlCompiler.SegmentEstimate"/>.</summary>
        public int Il;
    }

    /// <summary>Translates EVM bytecode into a dynamic method with the contract of <see cref="RunDispatchLoop{TTracingInst, TCancelable}"/>.</summary>
    /// <remarks>
    /// A segment, a straight run of opcodes compiled inline or through checked bodies, charges its static gas and checks its stack
    /// bounds once, at its start; any other opcode calls the interpreter's body with the exact gas and stack. The rest is handed back
    /// at an instruction boundary with gas, stack and memory exactly the interpreter's, so every failure and its error stay the interpreter's.
    /// </remarks>
    internal static partial class IlCompiler
    {
        private const int MaxCodeLength = 64 * 1024;
        /// <summary>RyuJIT compiles a method of more IL than this without optimizations (<c>DEFAULT_MIN_OPTS_CODE_AMOUNT</c> is 60000).</summary>
        private const int MaxIlBytes = 56_000;
        /// <summary>Kept under RyuJIT's <c>DEFAULT_MIN_OPTS_BB_COUNT</c> of 2000 basic blocks, with headroom for those the JIT adds.</summary>
        private const int MaxLabels = 1_700;
        /// <summary>Kept under RyuJIT's <c>DEFAULT_MIN_OPTS_INSTR_COUNT</c> of 20000 IL instructions.</summary>
        private const int MaxIlInstructions = 18_000;
        /// <summary>Kept under RyuJIT's <c>DEFAULT_MIN_OPTS_LV_REF_COUNT</c> of 8000 references to locals and arguments.</summary>
        private const int MaxLocalReferences = 7_000;
        /// <summary>Kept under RyuJIT's <c>DEFAULT_MIN_OPTS_LV_NUM_COUNT</c> of 2000 locals.</summary>
        private const int MaxLocals = 1_500;
        private const int MinChunkInstructions = 40;
        /// <summary>A segment is cut once its estimate reaches this fraction of a chunk's, so chunks hold several and a long run spans chunks.</summary>
        private const int SegmentShare = 4;
        /// <summary>The smallest segment estimate the recut goes down to; a few instructions with their checks.</summary>
        private const int MinSegmentIl = 120;

        /// <summary>
        /// The most CPU time a layout may take, every attempt and chunk emitted included, before the contract is declined:
        /// a contract of 24 KiB lays out in tens of milliseconds, so only code shaped to cost more meets it. CPU time, not
        /// wall-clock time, which a busy machine's preemption stretches: legitimate contracts must not fail for that, as a
        /// failure is remembered (<see cref="IlThreadTime"/>). Settable for tests.
        /// </summary>
        internal static TimeSpan LayoutBudget = TimeSpan.FromSeconds(1);

        /// <summary>Whether a layout begun at <paramref name="started"/>, with its thread's CPU time at <paramref name="cpuStarted"/>, ran past <see cref="LayoutBudget"/>.</summary>
        /// <remarks>Wall-clock time first, which costs no system call: a thread runs no faster than the clock. Where the platform tells no CPU time, wall-clock time decides.</remarks>
        private static bool PastLayoutBudget(long started, double cpuStarted)
        {
            double budget = LayoutBudget.TotalMilliseconds;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < budget)
                return false;
            double cpu = IlThreadTime.Ms();
            return double.IsNaN(cpu) || double.IsNaN(cpuStarted) || cpu - cpuStarted >= budget;
        }

        private static readonly FieldInfo GasValue = typeof(EthereumGasPolicy).GetField(nameof(EthereumGasPolicy.Value))!;
        private static readonly FieldInfo StackHead = typeof(EvmStack).GetField(nameof(EvmStack.Head))!;
        /// <summary>The stack's word storage, which the chunks address slots from: a ref-returning getter the JIT inlines to the field load.</summary>
        private static readonly MethodInfo StackBase = typeof(EvmStack).GetProperty(nameof(EvmStack.StackBase))!.GetGetMethod()!;
        /// <summary>The frame's input data and its length, which the CALLDATALOAD fast path reads; getters the JIT inlines likewise.</summary>
        private static readonly MethodInfo StackInputData = typeof(EvmStack).GetProperty(nameof(EvmStack.InputDataStart))!.GetGetMethod()!;
        private static readonly MethodInfo StackInputDataLength = typeof(EvmStack).GetProperty(nameof(EvmStack.InputDataSize))!.GetGetMethod()!;
        private static readonly MethodInfo EmbeddedMethod = typeof(IlEvm).GetMethod(nameof(IlEmbedded), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly Type[] BodySignature = [typeof(EvmStack).MakeByRefType(), typeof(EthereumGasPolicy).MakeByRefType(), typeof(VirtualMachine<EthereumGasPolicy>), typeof(nint).MakeByRefType()];

        private static readonly FieldInfo EntryIndexField = typeof(IlChunk).GetField(nameof(IlChunk.EntryIndex))!;
        private static readonly FieldInfo JumpOrdinalField = typeof(IlChunk).GetField(nameof(IlChunk.JumpOrdinal))!;
        private static readonly FieldInfo JumpCasesField = typeof(IlChunk).GetField(nameof(IlChunk.JumpCases))!;
        private static readonly FieldInfo JumpBaseField = typeof(IlChunk).GetField(nameof(IlChunk.JumpBase))!;
        private static readonly FieldInfo StateOpCodes = typeof(IlRunState).GetField(nameof(IlRunState.OpCodes))!;
        private static readonly FieldInfo StatePollAt = typeof(IlRunState).GetField(nameof(IlRunState.PollAt))!;
        private static readonly FieldInfo StateCounted = typeof(IlRunState).GetField(nameof(IlRunState.Counted))!;
        private static readonly MethodInfo FailAtMethod = typeof(IlChunk).GetMethod(nameof(IlChunk.FailAt), BindingFlags.Public | BindingFlags.Instance)!;
        /// <summary>Adds to the machine's opcode count; the JIT inlines it to the counter's load, add and store.</summary>
        private static readonly MethodInfo AddOpCodes = typeof(VirtualMachine<EthereumGasPolicy>).GetMethod(nameof(VirtualMachine<EthereumGasPolicy>.AddOpCodes), [typeof(int)])!;
        private static readonly MethodInfo PollCancellation = typeof(IlEvmFrameRunner).GetMethod(nameof(IlEvmFrameRunner.Poll), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo MemoryOf = typeof(IlEvm).GetMethod(nameof(IlMemoryOf), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMLoad = typeof(IlEvm).GetMethod(nameof(IlTryMLoad), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMStore = typeof(IlEvm).GetMethod(nameof(IlTryMStore), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMLoadAt = typeof(IlEvm).GetMethod(nameof(IlTryMLoadAt), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo CallDataLoadAt = typeof(IlEvm).GetMethod(nameof(IlCallDataLoadAt), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMStoreAt = typeof(IlEvm).GetMethod(nameof(IlTryMStoreAt), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMStoreWordAt = typeof(IlEvm).GetMethod(nameof(IlTryMStoreWordAt), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo CarryMethod = typeof(IlEvm).GetMethod(nameof(IlCarry), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo VectorCreate = typeof(Vector256).GetMethod(nameof(Vector256.Create), [typeof(ulong), typeof(ulong), typeof(ulong), typeof(ulong)])!;
        private static readonly MethodInfo VectorCreateScalar = typeof(Vector256).GetMethod(nameof(Vector256.CreateScalar), [typeof(ulong)])!;
        private static readonly MethodInfo VectorAsByte = typeof(Vector256).GetMethod(nameof(Vector256.AsByte))!.MakeGenericMethod(typeof(ulong));
        private static readonly MethodInfo VectorOnesComplement = typeof(Vector256).GetMethod(nameof(Vector256.OnesComplement))!.MakeGenericMethod(typeof(byte));
        private static readonly MethodInfo BorrowMethod = typeof(IlEvm).GetMethod(nameof(IlBorrow), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodInfo VectorMethod(string name, Type element, int parameters) =>
            typeof(Vector256).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == parameters &&
                             m.GetParameters().All(static p => p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Vector256<>)))
                .MakeGenericMethod(element);

        private static readonly MethodInfo VecAdd = VectorMethod(nameof(Vector256.Add), typeof(ulong), 2);
        private static readonly MethodInfo VecSubtract = VectorMethod(nameof(Vector256.Subtract), typeof(ulong), 2);
        private static readonly MethodInfo VecAnd = VectorMethod(nameof(Vector256.BitwiseAnd), typeof(ulong), 2);
        private static readonly MethodInfo VecOr = VectorMethod(nameof(Vector256.BitwiseOr), typeof(ulong), 2);
        private static readonly MethodInfo VecXor = VectorMethod(nameof(Vector256.Xor), typeof(ulong), 2);
        private static readonly MethodInfo VecEqualsAll = VectorMethod(nameof(Vector256.EqualsAll), typeof(ulong), 2);
        private static readonly MethodInfo VecToScalar = VectorMethod(nameof(Vector256.ToScalar), typeof(ulong), 1);
        private static readonly MethodInfo VecAsInt64 = VectorMethod(nameof(Vector256.AsInt64), typeof(ulong), 1);
        private static readonly MethodInfo VecGreaterThanSigned = VectorMethod(nameof(Vector256.GreaterThan), typeof(long), 2);
        private static readonly MethodInfo VecMaskSigned = VectorMethod(nameof(Vector256.ExtractMostSignificantBits), typeof(long), 1);
        private static readonly MethodInfo VecZero = typeof(Vector256<ulong>).GetProperty(nameof(Vector256<ulong>.Zero))!.GetGetMethod()!;
        private static readonly MethodInfo VecOnesComplement = VectorMethod(nameof(Vector256.OnesComplement), typeof(ulong), 1);
        private static readonly MethodInfo VecGetElement = typeof(Vector256).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(static m => m.Name == nameof(Vector256.GetElement) && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(ulong));
        private static readonly MethodInfo CarryVectorMethod = typeof(IlEvm).GetMethod(nameof(IlCarryVector), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo CallDataWordMethod = typeof(IlEvm).GetMethod(nameof(IlCallDataWord), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo VecAsByte = VectorMethod(nameof(Vector256.AsByte), typeof(ulong), 1);
        private static readonly MethodInfo VecBytesAsUInt64 = VectorMethod(nameof(Vector256.AsUInt64), typeof(byte), 1);
        private static readonly MethodInfo Permute4x64 = typeof(System.Runtime.Intrinsics.X86.Avx2).GetMethod(nameof(System.Runtime.Intrinsics.X86.Avx2.Permute4x64), [typeof(Vector256<ulong>), typeof(byte)])!;
        private static readonly MethodInfo ShuffleBytes = typeof(System.Runtime.Intrinsics.X86.Avx2).GetMethod(nameof(System.Runtime.Intrinsics.X86.Avx2.Shuffle), [typeof(Vector256<byte>), typeof(Vector256<byte>)])!;
        private static readonly MethodInfo TestZ = typeof(System.Runtime.Intrinsics.X86.Avx).GetMethod(nameof(System.Runtime.Intrinsics.X86.Avx.TestZ), [typeof(Vector256<ulong>), typeof(Vector256<ulong>)])!;
        private static readonly MethodInfo BorrowVectorMethod = typeof(IlEvm).GetMethod(nameof(IlBorrowVector), BindingFlags.NonPublic | BindingFlags.Static)!;

        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
        internal static IlContract? Compile(CodeInfo codeInfo, IlFork fork, out string? reason)
        {
            if (fork.UsesCallDestinations)
            {
                reason = "EIP-7979 subroutines";
                return null;
            }

            ReadOnlySpan<byte> code = codeInfo.CodeSpan;
            if (code.Length == 0 || code.Length > MaxCodeLength)
            {
                reason = "code length";
                return null;
            }

            IlOp[] ops = fork.Ops;
            List<IlInstruction> instructions = Decode(code, ops);
            FusePeepholes(instructions);

            // Chunks are cut small enough for the JIT to optimize and all emitted now, cheap next to the JIT; one too large has the whole contract cut again, smaller.
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            double cpuStarted = IlThreadTime.Ms();
            int instructionsPerChunk = IlEvmSettings.ChunkInstructions;
            int inlineBudget = IlEvmSettings.ChunkInlineBudget;
            int ilBudget = MaxIlEstimate;
            for (int attempt = 0; ; attempt++)
            {
                int[] jumpIndex = new int[code.Length];
                Array.Fill(jumpIndex, -1);
                List<IlUnit> units = Cut(instructions, ilBudget / SegmentShare);

                // The interpreter's bitmap decides where a jump may land: it may mark fewer JUMPDESTs than the code holds, never a non-JUMPDEST byte.
                int jumpTargets = 0;
                foreach (IlUnit unit in units)
                {
                    if (unit.IsJumpDest && !codeInfo.ValidateJump(unit.StartPc))
                        unit.IsJumpDest = false;
                    if (unit.IsJumpDest)
                        jumpIndex[unit.StartPc] = jumpTargets++;
                }

                for (int pc = 0; pc < code.Length; pc++)
                {
                    if (codeInfo.ValidateJump(pc) && jumpIndex[pc] < 0)
                    {
                        reason = $"jump destination analysis disagrees at {pc}";
                        return null;
                    }
                }

                units = Reachable(units);

                List<ulong> costs = [];
                List<int> inputs = [];
                List<int> growths = [];
                List<bool> dynamic = [];
                List<bool> opaque = [];
                foreach (IlUnit unit in units)
                {
                    if (!unit.IsSegment) continue;
                    unit.FirstTableIndex = costs.Count;
                    foreach (IlInstruction instruction in unit.Instructions)
                    {
                        costs.Add(instruction.Op.StaticGas);
                        inputs.Add(instruction.Op.StackInputs);
                        growths.Add(instruction.Op.StackGrowth);
                        dynamic.Add(instruction.Op.Dynamic);
                        opaque.Add(instruction.Op.Opaque);
                    }
                }

                Dictionary<int, IlUnit> unitsByPc = new(units.Count);
                foreach (IlUnit unit in units)
                    unitsByPc[unit.StartPc] = unit;

                List<List<IlUnit>> chunks = IlEvmSettings.Regions
                    ? Regions(units, unitsByPc, code.Length, instructionsPerChunk, inlineBudget, ilBudget, IlEvmSettings.ChunkDuplication)
                    : Partition(units, instructionsPerChunk, inlineBudget, ilBudget);
                // A unit's home is the first chunk that compiles it: frames enter, resume and continue there. A chunk's jump cases are its JUMPDESTs in order.
                int[] entryChunk = new int[code.Length];
                int[] entryIndex = new int[code.Length];
                Array.Fill(entryChunk, -1);
                // A chunk's table covers only its own JUMPDEST ordinal range, so tables grow with the code, not chunks times JUMPDESTs.
                short[][] jumpCases = new short[chunks.Count][];
                int[] jumpBases = new int[chunks.Count];
                long tableBytes = 12L * code.Length;
                int copies = 0;
                for (int c = 0; c < chunks.Count; c++)
                {
                    int lowest = int.MaxValue, highest = -1;
                    foreach (IlUnit unit in chunks[c])
                    {
                        if (!unit.IsJumpDest) continue;
                        lowest = Math.Min(lowest, jumpIndex[unit.StartPc]);
                        highest = Math.Max(highest, jumpIndex[unit.StartPc]);
                    }

                    short[] cases = jumpCases[c] = highest < 0 ? [] : new short[highest - lowest + 1];
                    jumpBases[c] = highest < 0 ? 0 : lowest;
                    tableBytes += 2L * cases.Length;
                    Array.Fill(cases, (short)-1);
                    short jumpCase = 0;
                    for (int u = 0; u < chunks[c].Count; u++)
                    {
                        IlUnit unit = chunks[c][u];
                        if (entryChunk[unit.StartPc] < 0)
                        {
                            entryChunk[unit.StartPc] = c;
                            entryIndex[unit.StartPc] = u;
                        }
                        else
                        {
                            copies++;
                        }

                        if (unit.IsJumpDest)
                            cases[jumpIndex[unit.StartPc] - jumpBases[c]] = jumpCase++;
                    }
                }

                string name = $"evm_{codeInfo.CodeSpan.FastHash():x8}";
                IlContract contract = new(fork, codeInfo.Code.ToArray(), entryChunk, entryIndex, jumpIndex, jumpCases, jumpBases, costs.ToArray(),
                    inputs.ToArray(), growths.ToArray(), dynamic.ToArray(), opaque.ToArray(), chunks.Count, name) { TableBytes = tableBytes };
                string? tooLarge = null;
                InlineBudget bodyBudget = new();
                for (int c = 0; c < chunks.Count && tooLarge is null; c++)
                {
                    contract.Prepared[c] = EmitChunk(contract, c, chunks[c], unitsByPc, bodyBudget, out tooLarge)!;
                    // Nothing was JIT-compiled yet; the emitted methods are dropped with the contract. A contract past its
                    // IL cap stops here, as the chunks left would only add to it.
                    if (contract.IlBytes > IlEvmSettings.MaxContractIlBytes)
                    {
                        reason = $"compiles to over {contract.IlBytes} bytes of IL, past {IlEvmSettings.MaxContractIlBytes}, at chunk {c + 1} of {chunks.Count}";
                        return null;
                    }

                    if (PastLayoutBudget(started, cpuStarted))
                    {
                        reason = $"layout past its budget of {LayoutBudget.TotalMilliseconds:F0} ms, at attempt {attempt + 1}, chunk {c + 1} of {chunks.Count}";
                        return null;
                    }
                }

                if (tooLarge is not null)
                {
                    if (ilBudget / SegmentShare <= MinSegmentIl || attempt > 12)
                    {
                        reason = tooLarge;
                        return null;
                    }

                    if (IlEvmDiagnostics.Enabled)
                        Console.WriteLine($"il-evm {name}: {tooLarge}; cutting again");
                    instructionsPerChunk = Math.Max(MinChunkInstructions, instructionsPerChunk * 3 / 4);
                    inlineBudget = Math.Max(16, inlineBudget * 3 / 4);
                    ilBudget = ilBudget * 3 / 4;
                    continue;
                }

                if (IlEvmDiagnostics.Enabled)
                    Console.WriteLine($"il-evm laid out {name}: {code.Length} B of code, {units.Count} units, {copies} copies, {chunks.Count} chunks, {contract.IlBytes / 1024} KiB IL");

                if (IlEvmSettings.EagerChunks)
                    contract.CompileAllChunks();
                reason = null;
                return contract;
            }
        }

        /// <summary>Emits one chunk of a laid-out contract and binds it, leaving the JIT to the first frames that reach it; null with <paramref name="failure"/> when too large to optimize.</summary>
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
        private static IlRunner? EmitChunk(IlContract contract, int index, List<IlUnit> units, Dictionary<int, IlUnit> unitsByPc, InlineBudget inlineBudget,
            out string? failure)
        {
            // skipVisibility: for the plugin's private helpers and the non-public body types' Execute; every other core member used is public.
            DynamicMethod method = new(
                $"{contract.Name}_{index}",
                typeof(EvmExceptionType),
                [typeof(IlChunk), typeof(VirtualMachine<EthereumGasPolicy>), typeof(EvmStack).MakeByRefType(), typeof(EthereumGasPolicy).MakeByRefType(), typeof(nint).MakeByRefType(), typeof(IlRunState).MakeByRefType()],
                typeof(IlEvm).Module,
                skipVisibility: true)
            {
                InitLocals = false
            };

            IlWriter generator = new(method.GetILGenerator()) { MethodName = method.Name };
            List<IlSegmentInfo> segments = [];
            List<IlSite> sites = [];
            Emitter emitter = new(generator, units, unitsByPc, contract.Code.Length, segments, sites, inlineBudget);
            emitter.Emit();
            // Past these the JIT compiles the method without optimizations, or takes very long to optimize it.
            if (generator.ILOffset > MaxIlBytes || emitter.Labels > MaxLabels || generator.Instructions > MaxIlInstructions ||
                generator.LocalReferences > MaxLocalReferences || generator.Locals > MaxLocals)
            {
                failure = $"chunk {index} too large to optimize: {units.Count} units, {generator.ILOffset}B/{generator.Instructions}i/{generator.LocalReferences}r/{generator.Locals}l/{emitter.Labels}b/{generator.InlineSites}s";
                return null;
            }

            IlChunk chunk = new(contract, contract.JumpCases[index], contract.JumpBases[index], segments.ToArray(), sites.ToArray());
            IlRunner runner = (IlRunner)method.CreateDelegate(typeof(IlRunner), chunk);
            contract.IlBytes += generator.ILOffset;
            failure = null;
            return runner;
        }

        /// <summary>The units execution can reach: the entry, what reached units fall through to (a hand-back counts, as the interpreter runs on and a CALL there resumes compiled), and every JUMPDEST a jump may land on.</summary>
        private static List<IlUnit> Reachable(List<IlUnit> units)
        {
            List<IlUnit> reachable = new(units.Count);
            bool fallsInto = true;
            foreach (IlUnit unit in units)
            {
                if (fallsInto || unit.IsJumpDest)
                {
                    reachable.Add(unit);
                    fallsInto = FallsThrough(unit);
                }
                else
                {
                    fallsInto = false;
                }
            }

            return reachable;
        }

        private static bool FallsThrough(IlUnit unit) =>
            unit.Instructions[^1].Op.Kind switch
            {
                IlKind.Stop or IlKind.Jump or IlKind.TerminatingBody => false,
                _ => true,
            };

        /// <summary>Where a unit leads, for region forming.</summary>
        private sealed class IlEdges
        {
            /// <summary>The JUMPDEST a constant destination jumps to.</summary>
            public IlUnit? Jump;
            /// <summary>The jump is a dispatcher's selector match, so its target starts an external function.</summary>
            public bool JumpStartsFunction;
            public IlUnit? Fall;
            /// <summary>JUMPDESTs whose offsets the unit pushes for later, the return addresses of internal calls.</summary>
            public readonly List<IlUnit> Continuations = [];
        }

        private static IlEdges Edges(IlUnit unit, Dictionary<int, IlUnit> unitsByPc, int codeLength)
        {
            IlEdges edges = new();
            IlInstruction last = unit.Instructions[^1];
            if (FallsThrough(unit) && unitsByPc.TryGetValue(last.Pc + last.Size, out IlUnit? fall))
                edges.Fall = fall;
            if (!unit.IsSegment)
                return edges;

            // The segment's stack from its start: small constants, whether a value is a 3- or 4-byte constant, and EQs against one (selector matches).
            List<(long Value, bool Selector, bool Match)> stack = [];
            (long Value, bool Selector, bool Match) unknown = (-1, false, false);
            List<long> pushed = [];

            (long Value, bool Selector, bool Match) Pop()
            {
                if (stack.Count == 0) return unknown;
                (long Value, bool Selector, bool Match) top = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return top;
            }

            (long Value, bool Selector, bool Match) Peek(int depth) => depth <= stack.Count ? stack[^depth] : unknown;

            bool IsTarget(long value, [NotNullWhen(true)] out IlUnit? target)
            {
                target = null;
                return value >= 0 && unitsByPc.TryGetValue((int)value, out target) && target.IsJumpDest;
            }

            foreach (IlInstruction instruction in unit.Instructions)
            {
                IlOp op = instruction.Op;
                switch (op.Kind)
                {
                    case IlKind.Push:
                    {
                        bool small = (instruction.U1 | instruction.U2 | instruction.U3) == 0 && instruction.U0 < (ulong)codeLength;
                        long value = small ? (long)instruction.U0 : -1;
                        stack.Add((value, op.Count is 3 or 4, false));
                        if (value >= 0)
                            pushed.Add(value);
                        break;
                    }
                    case IlKind.Dup:
                        stack.Add(Peek(op.Count));
                        break;
                    case IlKind.Swap:
                        if (op.Count + 1 <= stack.Count)
                            (stack[^1], stack[^(op.Count + 1)]) = (stack[^(op.Count + 1)], stack[^1]);
                        else if (stack.Count > 0)
                            stack[^1] = unknown;
                        break;
                    case IlKind.Pop:
                        Pop();
                        break;
                    case IlKind.Jump:
                        if (IsTarget(Pop().Value, out IlUnit? jumpTarget))
                            edges.Jump = jumpTarget;
                        break;
                    case IlKind.JumpIf:
                    {
                        (long destination, _, _) = Pop();
                        (_, _, bool match) = Pop();
                        if (IsTarget(destination, out IlUnit? target))
                        {
                            edges.Jump = target;
                            edges.JumpStartsFunction = match;
                        }

                        break;
                    }
                    default:
                    {
                        bool match = instruction.Opcode == (byte)Instruction.EQ && (Peek(1).Selector || Peek(2).Selector);
                        for (int k = 0; k < op.StackInputs; k++)
                            Pop();
                        for (int k = 0; k < op.StackInputs + op.StackGrowth; k++)
                            stack.Add((-1, false, match));
                        break;
                    }
                }
            }

            // Each target once, in the order first pushed: a segment may push hundreds, so past a few a set tells repeats.
            HashSet<IlUnit>? seen = pushed.Count > 8 ? new(pushed.Count) : null;
            foreach (long value in pushed)
            {
                if (IsTarget(value, out IlUnit? continuation) && continuation != edges.Jump &&
                    (seen is null ? !edges.Continuations.Contains(continuation) : seen.Add(continuation)))
                    edges.Continuations.Add(continuation);
            }

            return edges;
        }

        /// <summary>Cuts the units into chunks along control flow, each grown depth first from a root as execution would go.</summary>
        /// <remarks>Jumps are followed before return addresses, so a call and its return share a method; selector matches root their own chunks; a unit homed elsewhere is copied, up to <paramref name="duplication"/> per unit, else continued at.</remarks>
        private static List<List<IlUnit>> Regions(List<IlUnit> units, Dictionary<int, IlUnit> unitsByPc, int codeLength, int instructions, int inlineBudget, int ilBudget, double duplication)
        {
            Dictionary<IlUnit, IlEdges> edges = new(units.Count);
            foreach (IlUnit unit in units)
                edges[unit] = Edges(unit, unitsByPc, codeLength);

            HashSet<IlUnit> homed = new(units.Count);
            int copies = 0;
            int maxCopies = (int)Math.Min(int.MaxValue, units.Count * duplication);
            List<List<IlUnit>> regions = [];
            Queue<IlUnit> roots = new();
            roots.Enqueue(units[0]);
            int nextUnhomed = 0;
            while (true)
            {
                IlUnit? root = null;
                while (roots.Count > 0)
                {
                    IlUnit candidate = roots.Dequeue();
                    if (!homed.Contains(candidate))
                    {
                        root = candidate;
                        break;
                    }
                }

                if (root is null)
                {
                    while (nextUnhomed < units.Count && homed.Contains(units[nextUnhomed]))
                        nextUnhomed++;
                    if (nextUnhomed == units.Count)
                        break;
                    root = units[nextUnhomed];
                }

                List<IlUnit> region = [];
                HashSet<IlUnit> members = [];
                int count = 0;
                int weight = 0;
                int il = 0;
                Stack<IlUnit> walk = new();
                walk.Push(root);
                while (walk.Count > 0)
                {
                    IlUnit unit = walk.Pop();
                    if (members.Contains(unit))
                        continue;

                    bool copy = homed.Contains(unit);
                    if (copy && copies >= maxCopies)
                        continue;

                    int unitWeight = 0;
                    foreach (IlInstruction instruction in unit.Instructions)
                        unitWeight += InlineWeight(instruction);
                    int unitIl = IlEstimate(unit);
                    if (region.Count > 0 && (count + unit.Instructions.Count > instructions || weight + unitWeight > inlineBudget || il + unitIl > ilBudget))
                    {
                        if (!copy)
                            roots.Enqueue(unit);
                        continue;
                    }

                    region.Add(unit);
                    members.Add(unit);
                    count += unit.Instructions.Count;
                    weight += unitWeight;
                    il += unitIl;
                    if (copy)
                        copies++;
                    else
                        homed.Add(unit);

                    // Popped in reverse: the jump first, then what the unit falls into, then the return addresses.
                    IlEdges next = edges[unit];
                    for (int i = next.Continuations.Count - 1; i >= 0; i--)
                        walk.Push(next.Continuations[i]);
                    if (next.Fall is not null)
                        walk.Push(next.Fall);
                    if (next.Jump is not null)
                    {
                        if (next.JumpStartsFunction)
                            roots.Enqueue(next.Jump);
                        else
                            walk.Push(next.Jump);
                    }
                }

                // In code order, so a unit and the one it falls into are adjacent wherever both are here.
                region.Sort(static (a, b) => a.StartPc.CompareTo(b.StartPc));
                regions.Add(region);
            }

            return regions;
        }

        /// <summary>Groups consecutive units into chunks of at most <paramref name="instructions"/> instructions, units kept whole.</summary>
        private static List<List<IlUnit>> Partition(List<IlUnit> units, int instructions, int inlineBudget, int ilBudget)
        {
            List<List<IlUnit>> chunks = [];
            List<IlUnit> current = [];
            int count = 0;
            int weight = 0;
            int il = 0;
            foreach (IlUnit unit in units)
            {
                int unitWeight = 0;
                foreach (IlInstruction instruction in unit.Instructions)
                    unitWeight += InlineWeight(instruction);

                int unitIl = IlEstimate(unit);
                if (count > 0 && (count + unit.Instructions.Count > instructions || weight + unitWeight > inlineBudget || il + unitIl > ilBudget))
                {
                    chunks.Add(current);
                    current = [];
                    count = 0;
                    weight = 0;
                    il = 0;
                }

                current.Add(unit);
                count += unit.Instructions.Count;
                weight += unitWeight;
                il += unitIl;
            }

            if (current.Count > 0)
                chunks.Add(current);
            return chunks;
        }

        /// <summary>The IL instructions a chunk is cut at, with headroom under <see cref="MaxIlInstructions"/> for the estimate's error.</summary>
        private const int MaxIlEstimate = 14_000;

        /// <summary>Roughly the IL instructions a unit compiles to, its cold paths included.</summary>
        private static int IlEstimate(IlUnit unit) => unit.IsSegment ? unit.Il : 60;

        /// <summary>Roughly the IL instructions of a segment, followed as the emitter goes: values kept out of their slots cost a write each when written out, and each embedded body's hand-back writes out all below it again.</summary>
        internal sealed class SegmentEstimate
        {
            /// <summary>IL instructions to write one virtual value to its slot: one vector store, or four limb stores without 256-bit vectors.</summary>
            private static readonly int Write = Vector256.IsHardwareAccelerated ? 12 : 26;

            private enum Held : byte { Memory, Constant, Flag }

            /// <summary>Whether the segment keeps words in vector locals (<see cref="IlEvmSettings.VectorForms"/>), whose forms are larger.</summary>
            private static bool Tracked => IlEvmSettings.VectorForms && IlEvmSettings.DirectArithmetic;

            /// <summary>What the stack positions hold relative to the segment's start, below it too as a SWAP can move values there; any not listed are in memory.</summary>
            private readonly Dictionary<int, Held> _held = [];
            private int _height;
            private int _virtual;
            private int _il = 30;

            /// <summary>The segment so far, with the writes of whatever it still holds virtually at its end.</summary>
            public int Total => _il + _virtual * Write;

            private Held At(int position) => _held.TryGetValue(position, out Held held) ? held : Held.Memory;

            private void Set(int position, Held value)
            {
                Held old = At(position);
                if (value == Held.Memory)
                    _held.Remove(position);
                else
                    _held[position] = value;
                _virtual += (value != Held.Memory ? 1 : 0) - (old != Held.Memory ? 1 : 0);
            }

            private void WriteOut(int position)
            {
                if (At(position) == Held.Memory)
                    return;
                Set(position, Held.Memory);
                _il += Write;
            }

            private int VirtualBelow(int limit)
            {
                int count = 0;
                foreach (int position in _held.Keys)
                {
                    if (position < limit)
                        count++;
                }

                return count;
            }

            /// <summary>Clears what an instruction consumed and produced in memory, from <paramref name="from"/> up.</summary>
            private void Produce(int from, int to)
            {
                for (int position = from; position < Math.Max(to, _height); position++)
                    Set(position, Held.Memory);
                _height = to;
            }

            public void Add(IlInstruction instruction)
            {
                IlOp op = instruction.Op;
                int h = _height;
                switch (op.Kind)
                {
                    case IlKind.Push:
                    case IlKind.ProgramCounter:
                        Set(h, Held.Constant);
                        _height = h + 1;
                        _il += 2;
                        break;
                    case IlKind.Dup:
                    {
                        Held source = At(h - op.Count);
                        Set(h, source);
                        _height = h + 1;
                        _il += source != Held.Memory ? 2 : 9;
                        break;
                    }
                    case IlKind.Swap:
                    {
                        Held a = At(h - 1), b = At(h - 1 - op.Count);
                        if (a != Held.Memory && b != Held.Memory)
                        {
                            _il += 1;
                        }
                        else if (a != Held.Memory || b != Held.Memory)
                        {
                            _il += 9;
                        }
                        else
                        {
                            _il += 18;
                        }

                        Set(h - 1, b);
                        Set(h - 1 - op.Count, a);
                        break;
                    }
                    case IlKind.Pop:
                        Set(h - 1, Held.Memory);
                        _height = h - 1;
                        _il += 2;
                        break;
                    case IlKind.JumpDest:
                        _il += 2;
                        break;
                    case IlKind.Gas:
                        Set(h, Held.Memory);
                        _height = h + 1;
                        _il += 22;
                        break;
                    case IlKind.CheckedBody when op.IsCallDataLoad && At(h - 1) == Held.Constant:
                        Set(h - 1, Held.Memory);
                        _il += 12;
                        break;
                    case IlKind.Native when op.StackInputs == 1:
                    {
                        Held x = At(h - 1);
                        if (x == Held.Constant)
                            break;
                        if (op.IsPredicate)
                        {
                            if (x == Held.Memory)
                                _il += 14;
                            Set(h - 1, Held.Flag);
                            break;
                        }

                        WriteOut(h - 1);
                        _il += 14;
                        break;
                    }
                    case IlKind.Native:
                    {
                        if (At(h - 1) == Held.Flag) WriteOut(h - 1);
                        if (At(h - 2) == Held.Flag) WriteOut(h - 2);
                        bool folds = At(h - 1) == Held.Constant && At(h - 2) == Held.Constant;
                        // A shift by a constant amount is a direct form over the limbs, several times a helper call.
                        bool constantShift = op.Family == IlFamily.Shift && At(h - 1) == Held.Constant;
                        // With the vector forms a bitwise result, and a constant shift's or addition's, stays in a local.
                        bool tracked = Tracked && !op.IsPredicate &&
                            (op.Family == IlFamily.Bitwise || ((op.Family == IlFamily.Shift || op.Family == IlFamily.Math2) && (At(h - 1) == Held.Constant || At(h - 2) == Held.Constant)));
                        Set(h - 1, Held.Memory);
                        _height = h - 1;
                        if (folds)
                        {
                            Set(h - 2, Held.Constant);
                            break;
                        }

                        Set(h - 2, op.IsPredicate ? Held.Flag : tracked ? Held.Constant : Held.Memory);
                        _il += constantShift ? 72 : op.IsPredicate && Tracked ? 36 : op.IsPredicate || IlEvmSettings.DirectArithmetic ? 24 : 14;
                        break;
                    }
                    case IlKind.MLoad:
                        // The fast path, and the body call on its cold path, which writes out a constant offset first.
                        _il += 40 + (At(h - 1) == Held.Constant ? Write : 0) + Write * VirtualBelow(h - 1);
                        Set(h - 1, Held.Memory);
                        break;
                    case IlKind.MStore:
                        _il += 50 + (At(h - 1) == Held.Constant ? Write : 0) + (At(h - 2) == Held.Constant ? Write : 0) + Write * VirtualBelow(h - 2);
                        Produce(h - 2, h - 2);
                        break;
                    case IlKind.CheckedBody when Tracked && op.IsCallDataLoad:
                        _il += 50;
                        Set(h - 1, Held.Constant);
                        break;
                    case IlKind.EmbeddedBody:
                    {
                        for (int position = h - op.StackInputs; position < h; position++)
                            WriteOut(position);
                        int after = h + op.StackGrowth;
                        _il += 40 + Write * VirtualBelow(after);
                        Produce(h - op.StackInputs, after);
                        break;
                    }
                    case IlKind.Stop:
                        _held.Clear();
                        _virtual = 0;
                        _height = 0;
                        _il += 10;
                        break;
                    case IlKind.Jump:
                    case IlKind.JumpIf:
                    {
                        // A constant destination is a branch; a computed one a table lookup with fault, foreign and poll paths.
                        bool constant = At(h - 1) == Held.Constant;
                        Produce(h - op.StackInputs, h - op.StackInputs);
                        _il += (constant ? 30 : 150) + Write * _virtual;
                        _held.Clear();
                        _virtual = 0;
                        _height = 0;
                        break;
                    }
                    default:
                        // A checked body, or an operation without a helper: its operands are written out, its results are in memory.
                        for (int position = h - op.StackInputs; position < h; position++)
                            WriteOut(position);
                        Produce(h - op.StackInputs, h + op.StackGrowth);
                        _il += 20;
                        break;
                }
            }
        }

        /// <summary>Roughly the JIT locals the inlined helpers of <paramref name="instruction"/> add to its chunk; anything run behind a call adds none.</summary>
        private static int InlineWeight(IlInstruction instruction) => instruction.Op.Kind switch
        {
            IlKind.Native when !IlEvmSettings.InlineArithmetic => 1,
            IlKind.Native => instruction.Op.Family is IlFamily.Bitwise or IlFamily.Math1 ? 4 : 8,
            IlKind.MLoad => IlEvmSettings.InlineMemory ? 8 : 1,
            IlKind.MStore => IlEvmSettings.InlineMemory ? 10 : 1,
            IlKind.CheckedBody => IlEvmSettings.InlineBodies && instruction.Op.Execute is not null ? 8 : 1,
            IlKind.JumpIf => 2,
            IlKind.Gas or IlKind.ProgramCounter => 1,
            _ => 0,
        };

        private static List<IlInstruction> Decode(ReadOnlySpan<byte> code, IlOp[] ops)
        {
            List<IlInstruction> instructions = new(code.Length / 2);
            Span<byte> word = stackalloc byte[32];
            for (int pc = 0; pc < code.Length;)
            {
                byte opcode = code[pc];
                IlOp op = ops[opcode];
                IlInstruction instruction = new() { Pc = pc, Opcode = opcode, Op = op, Size = 1 };
                if (opcode is >= (byte)Instruction.PUSH1 and <= (byte)Instruction.PUSH32)
                {
                    int count = opcode - (byte)Instruction.PUSH1 + 1;
                    instruction.Size = 1 + count;
                    // Big-endian immediate; bytes past the end of the code read as zero, as from the execution padding.
                    word.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        int source = pc + 1 + i;
                        word[32 - count + i] = source < code.Length ? code[source] : (byte)0;
                    }

                    instruction.U3 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(word);
                    instruction.U2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(word[8..]);
                    instruction.U1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(word[16..]);
                    instruction.U0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(word[24..]);
                }

                instructions.Add(instruction);
                pc += instruction.Size;
            }

            return instructions;
        }

        /// <summary>Models the untraced EXTCODESIZE body's peephole, which runs a following ISZERO, GT or EQ itself.</summary>
        /// <remarks>Before ISZERO it always does, so the pair is one instruction; before GT or EQ only when the word under the address is zero, so that EXTCODESIZE is a unit of its own going on past whatever the body ran.</remarks>
        private static void FusePeepholes(List<IlInstruction> instructions)
        {
            for (int i = 0; i + 1 < instructions.Count; i++)
            {
                IlInstruction instruction = instructions[i];
                if (instruction.Opcode != (byte)Instruction.EXTCODESIZE || instruction.Op.Kind != IlKind.EmbeddedBody)
                    continue;

                IlInstruction next = instructions[i + 1];
                if (next.Opcode == (byte)Instruction.ISZERO)
                {
                    instruction.Size += next.Size;
                    instructions.RemoveAt(i + 1);
                }
                else if (next.Opcode is (byte)Instruction.GT or (byte)Instruction.EQ)
                {
                    IlOp standalone = instruction.Op.Clone();
                    standalone.Kind = IlKind.Body;
                    instruction.Op = standalone;
                    instruction.MayFuseNext = true;
                    next.EndsSegment = true;
                }
            }
        }

        private static bool IsLinear(IlKind kind) => kind is IlKind.Push or IlKind.Dup or IlKind.Swap or IlKind.Pop or
            IlKind.JumpDest or IlKind.ProgramCounter or IlKind.Gas or IlKind.CheckedBody or IlKind.Native or
            IlKind.MLoad or IlKind.MStore or IlKind.EmbeddedBody;

        private static bool EndsSegment(IlKind kind) => kind is IlKind.Jump or IlKind.JumpIf or IlKind.Stop;

        /// <summary>Cuts the instructions into units, splitting a straight run past <paramref name="maxSegmentIl"/> estimated IL into segments falling into each other, so it spans chunks.</summary>
        private static List<IlUnit> Cut(List<IlInstruction> instructions, int maxSegmentIl)
        {
            List<IlUnit> units = [];
            IlUnit? segment = null;
            SegmentEstimate estimate = new();
            int delta = 0;

            void Close()
            {
                if (segment is null) return;
                segment.EndDelta = delta;
                segment.Il = estimate.Total;
                ulong remaining = segment.Gas;
                foreach (IlInstruction instruction in segment.Instructions)
                {
                    remaining -= instruction.Op.StaticGas;
                    instruction.GasAfter = remaining;
                }

                segment = null;
            }

            foreach (IlInstruction instruction in instructions)
            {
                IlKind kind = instruction.Op.Kind;
                if (IsLinear(kind) || EndsSegment(kind))
                {
                    if (kind == IlKind.JumpDest)
                        Close();

                    if (segment is null)
                    {
                        segment = new IlUnit { StartPc = instruction.Pc, IsSegment = true, IsJumpDest = kind == IlKind.JumpDest };
                        units.Add(segment);
                        estimate = new SegmentEstimate();
                        delta = 0;
                    }

                    IlOp op = instruction.Op;
                    segment.MinHeight = Math.Max(segment.MinHeight, op.StackInputs - delta);
                    if (op.StackGrowth > 0)
                        segment.MaxPeak = Math.Max(segment.MaxPeak, delta + op.StackGrowth);
                    segment.Gas += op.StaticGas;
                    segment.Instructions.Add(instruction);
                    estimate.Add(instruction);
                    delta += op.StackGrowth;

                    if (EndsSegment(kind) || instruction.EndsSegment || estimate.Total >= maxSegmentIl)
                        Close();
                    continue;
                }

                Close();
                IlUnit unit = new() { StartPc = instruction.Pc };
                unit.Instructions.Add(instruction);
                units.Add(unit);
            }

            Close();
            return units;
        }

        /// <summary>An <see cref="ILGenerator"/> that counts what RyuJIT weighs when it decides whether to optimize a method.</summary>
        private sealed class IlWriter(ILGenerator generator)
        {
            public int Instructions { get; private set; }

            /// <summary>Instructions that load, store or take the address of a local or an argument.</summary>
            public int LocalReferences { get; private set; }

            public int Locals { get; private set; }

            /// <summary>Calls to helpers marked for aggressive inlining.</summary>
            public int InlineSites { get; private set; }

            /// <summary>What the emitter is writing, for <see cref="IlEvmDiagnostics"/>; IL instructions are tallied under it.</summary>
            public string Category = "entry";

            public int ILOffset => generator.ILOffset;

            public LocalBuilder DeclareLocal(Type type)
            {
                Locals++;
                return generator.DeclareLocal(type);
            }

            public Label DefineLabel() => generator.DefineLabel();

            public void MarkLabel(Label label) => generator.MarkLabel(label);

            /// <summary>With <see cref="IlEvmDiagnostics.Markers"/>: the method's name, the code offset of the opcode being emitted, and the category last marked.</summary>
            public string MethodName = "";
            public int Pc = -1;
            private string? _marked;

            public void StoreMarker(int id)
            {
                generator.Emit(OpCodes.Ldc_I4, id);
                generator.Emit(OpCodes.Stsfld, MarkerField);
            }

            private static readonly FieldInfo MarkerField = typeof(IlEvmDiagnostics).GetField(nameof(IlEvmDiagnostics.Marker))!;

            private void Count(OpCode opcode)
            {
                Instructions++;
                if (IlEvmDiagnostics.Markers && !ReferenceEquals(_marked, Category))
                {
                    _marked = Category;
                    StoreMarker(IlEvmDiagnostics.NextMarker(MethodName, Category, Pc));
                }
                if (IlEvmDiagnostics.Enabled)
                    IlEvmDiagnostics.IlByCategory.AddOrUpdate(Category, 1, static (_, v) => v + 1);
                if (opcode.OperandType is OperandType.InlineVar or OperandType.ShortInlineVar ||
                    opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1 || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3 ||
                    opcode == OpCodes.Stloc_0 || opcode == OpCodes.Stloc_1 || opcode == OpCodes.Stloc_2 || opcode == OpCodes.Stloc_3 ||
                    opcode == OpCodes.Ldarg_0 || opcode == OpCodes.Ldarg_1 || opcode == OpCodes.Ldarg_2 || opcode == OpCodes.Ldarg_3)
                {
                    LocalReferences++;
                }
            }

            public void Emit(OpCode opcode)
            {
                Count(opcode);
                generator.Emit(opcode);
            }

            public void Emit(OpCode opcode, byte arg)
            {
                Count(opcode);
                generator.Emit(opcode, arg);
            }

            public void Emit(OpCode opcode, int arg)
            {
                Count(opcode);
                generator.Emit(opcode, arg);
            }

            public void Emit(OpCode opcode, long arg)
            {
                Count(opcode);
                generator.Emit(opcode, arg);
            }

            public void Emit(OpCode opcode, Label label)
            {
                Count(opcode);
                generator.Emit(opcode, label);
            }

            public void Emit(OpCode opcode, Label[] labels)
            {
                Count(opcode);
                generator.Emit(opcode, labels);
            }

            public void Emit(OpCode opcode, LocalBuilder local)
            {
                Count(opcode);
                generator.Emit(opcode, local);
            }

            public void Emit(OpCode opcode, FieldInfo field)
            {
                Count(opcode);
                generator.Emit(opcode, field);
            }

            public void Emit(OpCode opcode, MethodInfo method)
            {
                Count(opcode);
                if ((method.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining) != 0)
                    InlineSites++;
                generator.Emit(opcode, method);
            }

            public void Emit(OpCode opcode, Type type)
            {
                Count(opcode);
                generator.Emit(opcode, type);
            }

            /// <summary>A managed <c>calli</c> through the function pointer on the evaluation stack.</summary>
            public void EmitCalli(Type returnType, Type[] parameterTypes)
            {
                Count(OpCodes.Calli);
                generator.EmitCalli(OpCodes.Calli, CallingConventions.Standard, returnType, parameterTypes, null);
            }
        }

        /// <summary>The body call sites of a contract the JIT may still inline, shared by its chunks.</summary>
        internal sealed class InlineBudget
        {
            public int Remaining = IlEvmSettings.InlineBodyBudget;
        }
    }
}
