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
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    internal static partial class IlCompiler
    {
        private sealed class Emitter(IlWriter il, List<IlUnit> units, Dictionary<int, IlUnit> allUnits, int codeLength, List<IlSegmentInfo> segments, List<IlSite> sites, InlineBudget inlineBudget)
        {
            /// <summary>The units this chunk compiles; a jump to any other unit leaves the chunk for that unit's home.</summary>
            private readonly HashSet<IlUnit> _members = [.. units];
            /// <summary>Each unit's label in this chunk; a unit compiled into several chunks has one in each.</summary>
            private readonly Dictionary<IlUnit, Label> _unitLabels = new(units.Count);

            private Label LabelOf(IlUnit unit) => _unitLabels[unit];
            /// <summary>The <see cref="IlChunk"/> the chunk's method is bound to.</summary>
            private const byte ArgChunk = 0;
            private const byte ArgVm = 1;
            private const byte ArgStack = 2;
            private const byte ArgGas = 3;
            private const byte ArgProgramCounter = 4;
            private const byte ArgState = 5;

            private readonly List<Action> _outOfLine = [];

            private void AddOutOfLine(Action block)
            {
                string category = "cold " + il.Category;
                _outOfLine.Add(() =>
                {
                    il.Category = category;
                    block();
                });
            }
            private readonly List<Label> _jumpTargets = [];

            private LocalBuilder _gas = null!;
            private LocalBuilder _head = null!;
            private LocalBuilder _base = null!;
            private LocalBuilder _frame = null!;
            private LocalBuilder _pc = null!;
            private LocalBuilder _result = null!;
            private LocalBuilder _target = null!;
            private LocalBuilder _limb = null!;
            private LocalBuilder _word = null!;
            // Address-taken by the cold calls that write through them, so the hot locals they feed are not.
            private LocalBuilder _failGas = null!;
            private LocalBuilder _failOps = null!;
            private LocalBuilder? _memory;
            // The segment whose precondition failed, and the opcode count change of a body that left, for the shared paths.
            private LocalBuilder _failSegment = null!;
            private LocalBuilder _opsAdjust = null!;
            private Label _sharedFail;
            private Label _sharedContinue;
            private Label _leaveSite;
            private bool _usesSharedFail;
            private bool _usesSharedContinue;
            private bool _usesLeaveSite;
            private Label _exit;
            private Label _dynamicJump;
            private int _labels;

            /// <summary>Labels defined, a proxy for the basic blocks the JIT will see.</summary>
            public int Labels => _labels;

            private Label NewLabel()
            {
                _labels++;
                return il.DefineLabel();
            }

            public void Emit()
            {
                _gas = il.DeclareLocal(typeof(ulong));
                _head = il.DeclareLocal(typeof(nint));
                _base = il.DeclareLocal(typeof(byte).MakeByRefType());
                _frame = il.DeclareLocal(typeof(byte).MakeByRefType());
                _pc = il.DeclareLocal(typeof(nint));
                _result = il.DeclareLocal(typeof(EvmExceptionType));
                _target = il.DeclareLocal(typeof(int));
                _limb = il.DeclareLocal(typeof(ulong));
                _failGas = il.DeclareLocal(typeof(ulong));
                _failOps = il.DeclareLocal(typeof(int));
                _failSegment = il.DeclareLocal(typeof(int));
                _opsAdjust = il.DeclareLocal(typeof(int));
                foreach (IlUnit unit in units)
                {
                    foreach (IlInstruction instruction in unit.Instructions)
                    {
                        if (instruction.Op.Kind is IlKind.MLoad or IlKind.MStore)
                            _memory ??= il.DeclareLocal(typeof(EvmPooledMemory).MakeByRefType());
                    }
                }
                _word = il.DeclareLocal(typeof(EvmWord));
                _exit = NewLabel();
                _dynamicJump = NewLabel();
                _sharedFail = NewLabel();
                _sharedContinue = NewLabel();
                _leaveSite = NewLabel();

                foreach (IlUnit unit in units)
                {
                    Label label = _unitLabels[unit] = NewLabel();
                    if (unit.IsJumpDest)
                        _jumpTargets.Add(label);
                }

                EmitEntry();
                for (int i = 0; i < units.Count; i++)
                {
                    IlUnit unit = units[i];
                    if (unit.IsSegment)
                        EmitSegment(unit);
                    else
                        EmitSingle(unit);

                    // A unit falling into one not emitted right after it goes there; running off the end of the code halts as on STOP.
                    if (!FallsThrough(unit))
                        continue;
                    IlInstruction last = unit.Instructions[^1];
                    int fallPc = last.Pc + last.Size;
                    if (i + 1 < units.Count && units[i + 1].StartPc == fallPc)
                        continue;
                    il.Category = "fall through";
                    if (allUnits.TryGetValue(fallPc, out IlUnit? fall))
                        GoTo(fall);
                    else
                        ExitWith(EvmExceptionType.None, codeLength, 0, 0);
                }

                // Out-of-line blocks may add more of their own, so the list is walked by index.
                for (int block = 0; block < _outOfLine.Count; block++)
                    _outOfLine[block]();

                il.Category = "shared paths";
                if (_usesSharedFail)
                    EmitSharedFail();
                if (_usesSharedContinue)
                    EmitSharedContinue();
                if (_usesLeaveSite)
                    EmitLeaveSite();
                il.Category = "dynamic jump + exit";
                EmitDynamicJump();
                EmitExit();
            }

            /// <summary>Where a segment whose precondition failed goes: the walk finds the fault, or the interpreter runs the segment.</summary>
            private void EmitSharedFail()
            {
                il.MarkLabel(_sharedFail);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldloc, _failSegment);
                il.Emit(OpCodes.Ldloc, _gas);
                il.Emit(OpCodes.Ldloc, _head);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Ldloca, _failGas);
                il.Emit(OpCodes.Ldloca, _failOps);
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Call, FailAtMethod);
                il.Emit(OpCodes.Stloc, _result);
                Label fromStart = NewLabel();
                il.Emit(OpCodes.Ldloc, _result);
                il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                il.Emit(OpCodes.Beq, fromStart);
                il.Emit(OpCodes.Ldloc, _failGas);
                il.Emit(OpCodes.Stloc, _gas);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldloc, _failOps);
                il.Emit(OpCodes.Stfld, StateOpCodes);
                // fromStart: nothing of the segment ran, so its starting gas and head are the interpreter's.
                il.MarkLabel(fromStart);
                SyncHead(0);
                SyncGas(0);
                il.Emit(OpCodes.Br, _exit);
            }

            /// <summary>Where the chunk leaves for a unit of another chunk, whose offset is in the target local; the head is already moved.</summary>
            private void EmitSharedContinue()
            {
                il.MarkLabel(_sharedContinue);
                SyncHead(0);
                SyncGas(0);
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stind_I);
                il.Emit(OpCodes.Ldc_I4, (int)IlContinue);
                il.Emit(OpCodes.Stloc, _result);
                il.Emit(OpCodes.Br, _exit);
            }

            /// <summary>Where an embedded body that ended the compiled run goes; gas, head and counter are already the frame's.</summary>
            private void EmitLeaveSite()
            {
                il.MarkLabel(_leaveSite);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Ldloc, _opsAdjust);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stfld, StateOpCodes);
                il.Emit(OpCodes.Br, _exit);
            }

            private void EmitEntry()
            {
                Label inRange = NewLabel();
                // Outside the code the interpreter answers. A local of its own, as bodies take the address of the counter local.
                LocalBuilder entryPc = il.DeclareLocal(typeof(nint));
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldind_I);
                il.Emit(OpCodes.Stloc, entryPc);
                il.Emit(OpCodes.Ldloc, entryPc);
                il.Emit(OpCodes.Ldc_I4, codeLength);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Blt_Un, inRange);
                il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(inRange);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldfld, GasValue);
                il.Emit(OpCodes.Stloc, _gas);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldfld, StackHead);
                il.Emit(OpCodes.Stloc, _head);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Call, StackBase);
                il.Emit(OpCodes.Stloc, _base);
                if (_memory is not null)
                {
                    il.Emit(OpCodes.Ldarg_S, ArgVm);
                    il.Emit(OpCodes.Call, MemoryOf);
                    il.Emit(OpCodes.Stloc, _memory);
                }
                // The chain's opcode count and poll mark stay in the run state, in memory: as locals the JIT moved them at nearly every block boundary.

                Label[] entries = new Label[units.Count];
                for (int i = 0; i < units.Count; i++)
                    entries[i] = LabelOf(units[i]);

                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, EntryIndexField);
                il.Emit(OpCodes.Ldloc, entryPc);
                il.Emit(OpCodes.Ldelem_I4);
                il.Emit(OpCodes.Switch, entries);
                // No unit starts here (the frame resumed inside one): the interpreter takes it from this offset.
                il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                il.Emit(OpCodes.Ret);
            }

            private static MethodInfo Arithmetic(MethodInfo helper) => IlEvmSettings.InlineArithmetic ? helper : IlCalledTwin(helper);

            private static MethodInfo Memory(MethodInfo helper) => IlEvmSettings.InlineMemory ? helper : IlCalledTwin(helper);

            private void EmitSegment(IlUnit segment)
            {
                Label deopt = NewLabel();
                il.MarkLabel(LabelOf(segment));
                il.Pc = segment.StartPc;
                il.Category = "segment checks";

                bool checks = segment.Gas > 0 || segment.MinHeight > 0 || segment.MaxPeak > 0;
                if (segment.Gas > 0)
                {
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Ldc_I8, (long)segment.Gas);
                    il.Emit(OpCodes.Blt_Un, deopt);
                }

                if (segment.MinHeight > 0 && segment.MaxPeak > 0 && EvmStack.MaxStackSize - 1 - segment.MaxPeak < segment.MinHeight)
                {
                    // No head satisfies both bounds: the segment always faults, which the walk below reproduces.
                    il.Emit(OpCodes.Br, deopt);
                }
                else if (segment.MinHeight > 0 && segment.MaxPeak > 0)
                {
                    // Both bounds in one unsigned compare: head - min must lie in [0, max - min], where max is the highest
                    // head from which every push of the segment still fits below EvmStack.MaxStackSize.
                    il.Emit(OpCodes.Ldloc, _head);
                    il.Emit(OpCodes.Ldc_I4, segment.MinHeight);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Sub);
                    il.Emit(OpCodes.Ldc_I4, EvmStack.MaxStackSize - 1 - segment.MaxPeak - segment.MinHeight);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Bgt_Un, deopt);
                }
                else if (segment.MinHeight > 0)
                {
                    il.Emit(OpCodes.Ldloc, _head);
                    il.Emit(OpCodes.Ldc_I4, segment.MinHeight);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Blt, deopt);
                }
                else if (segment.MaxPeak > 0)
                {
                    // A push needs head + growth below EvmStack.MaxStackSize.
                    il.Emit(OpCodes.Ldloc, _head);
                    il.Emit(OpCodes.Ldc_I4, EvmStack.MaxStackSize - 1 - segment.MaxPeak);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Bgt, deopt);
                }

                if (segment.Gas > 0)
                {
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Ldc_I8, (long)segment.Gas);
                    il.Emit(OpCodes.Sub);
                    il.Emit(OpCodes.Stloc, _gas);
                }

                AddOps(segment.Instructions.Count);
                SetFrame();

                if (checks)
                {
                    int segmentId = segments.Count;
                    segments.Add(new IlSegmentInfo(segment.FirstTableIndex, segment.Instructions.Count, segment.StartPc));
                    _usesSharedFail = true;
                    AddOutOfLine(() =>
                    {
                        il.MarkLabel(deopt);
                        il.Emit(OpCodes.Ldc_I4, segmentId);
                        il.Emit(OpCodes.Stloc, _failSegment);
                        il.Emit(OpCodes.Br, _sharedFail);
                    });
                }

                int delta = 0;
                List<IlInstruction> instructions = segment.Instructions;
                for (int i = 0; i < instructions.Count; i++)
                {
                    IlInstruction instruction = instructions[i];
                    IlOp op = instruction.Op;
                    if (IlEvmDiagnostics.Markers)
                    {
                        il.Category = ((Instruction)instruction.Opcode).ToString();
                        il.Pc = instruction.Pc;
                    }
                    else if (IlEvmDiagnostics.Enabled)
                        il.Category = op.Kind is IlKind.Native or IlKind.CheckedBody or IlKind.EmbeddedBody ? $"{op.Kind} {op.Name}" : op.Kind.ToString();
                    switch (op.Kind)
                    {
                        case IlKind.Push:
                            SetValue(delta, VVal.Constant(instruction.U0, instruction.U1, instruction.U2, instruction.U3));
                            delta++;
                            break;
                        case IlKind.ProgramCounter:
                            SetValue(delta, VVal.Constant((ulong)instruction.Pc, 0, 0, 0));
                            delta++;
                            break;
                        case IlKind.Dup when _vstack:
                        {
                            // The copy is the source's value or its slot's alias. Resolve the copy's slot aliases first: the source may be one, and a spill may write it out and free its local.
                            int source = delta - op.Count;
                            if (!_virtual.ContainsKey(delta))
                                ResolveAliasesOf(delta);
                            SetValue(delta, Virtual(source) ?? VVal.AliasOf(source));
                            delta++;
                            break;
                        }
                        case IlKind.Swap when _vstack:
                            SwapValues(delta - op.Upper, delta - op.Upper - op.Count);
                            break;
                        case IlKind.Dup:
                        {
                            int source = delta - op.Count;
                            if (Virtual(source) is { } value)
                            {
                                _virtual[delta] = value;
                            }
                            else
                            {
                                _virtual.Remove(delta);
                                CopySlot(delta, source);
                            }
                            delta++;
                            break;
                        }
                        case IlKind.Swap:
                        {
                            int a = delta - op.Upper;
                            int b = a - op.Count;
                            VVal? va = Virtual(a);
                            VVal? vb = Virtual(b);
                            if (va is not null && vb is not null)
                            {
                                _virtual[a] = vb;
                                _virtual[b] = va;
                            }
                            else if (va is not null)
                            {
                                CopySlot(a, b);
                                _virtual.Remove(a);
                                _virtual[b] = va;
                            }
                            else if (vb is not null)
                            {
                                CopySlot(b, a);
                                _virtual.Remove(b);
                                _virtual[a] = vb;
                            }
                            else
                            {
                                Swap(a, b);
                            }
                            break;
                        }
                        case IlKind.Pop:
                            _virtual.Remove(delta - 1);
                            delta--;
                            break;
                        case IlKind.JumpDest:
                            break;
                        case IlKind.Gas when _vstack:
                            // GAS reads what the interpreter would hold after it: the segment charged the rest already.
                            il.Emit(OpCodes.Ldloc, _gas);
                            if (instruction.GasAfter > 0)
                            {
                                il.Emit(OpCodes.Ldc_I8, (long)instruction.GasAfter);
                                il.Emit(OpCodes.Add);
                            }

                            il.Emit(OpCodes.Call, VectorCreateScalar);
                            il.Emit(OpCodes.Stloc, VecScratch);
                            BindScratch(delta);
                            delta++;
                            break;
                        case IlKind.Gas:
                            _virtual.Remove(delta);
                            StoreSmallWord(delta, () =>
                            {
                                il.Emit(OpCodes.Ldloc, _gas);
                                if (instruction.GasAfter > 0)
                                {
                                    il.Emit(OpCodes.Ldc_I8, (long)instruction.GasAfter);
                                    il.Emit(OpCodes.Add);
                                }
                            });
                            delta++;
                            break;
                        case IlKind.CheckedBody when op.IsCallDataLoad && Virtual(delta - 1) is { Kind: VKind.Const } offset:
                            if (offset.U1 != 0 || offset.U2 != 0 || offset.U3 != 0)
                            {
                                // Past 2^64 the body reads nothing and pushes zero.
                                _virtual[delta - 1] = VVal.Constant(0, 0, 0, 0);
                            }
                            else
                            {
                                _virtual.Remove(delta - 1);
                                BeforeWrite(delta - 1, delta);
                                il.Emit(OpCodes.Ldarg_S, ArgStack);
                                il.Emit(OpCodes.Call, StackInputData);
                                il.Emit(OpCodes.Ldarg_S, ArgStack);
                                il.Emit(OpCodes.Call, StackInputDataLength);
                                il.Emit(OpCodes.Ldc_I8, (long)offset.U0);
                                Slot(delta - 1);
                                il.Emit(OpCodes.Call, Memory(CallDataLoadAt));
                            }
                            break;
                        case IlKind.CheckedBody when op.IsCallDataLoad && _vstack:
                            EmitCallDataLoadTracked(delta);
                            break;
                        case IlKind.CheckedBody:
                            for (int position = delta - op.StackInputs; position < delta; position++)
                                Materialize(position);
                            BeforeWrite(delta - op.StackInputs, Math.Max(delta, delta + op.StackGrowth));
                            SyncHead(delta);
                            CallBody(instruction, op);
                            il.Emit(OpCodes.Pop);
                            delta += op.StackGrowth;
                            break;
                        case IlKind.Native when op.Native is null && !(IlEvmSettings.DirectArithmetic &&
                            Virtual(delta - 1) is { Kind: VKind.Const } && (_vstack || Virtual(delta - 2) is null or { Kind: VKind.Const })):
                            // An operation the plugin has no helper for, without the constant amount its direct form needs.
                            for (int position = delta - op.StackInputs; position < delta; position++)
                                Materialize(position);
                            BeforeWrite(delta - op.StackInputs, Math.Max(delta, delta + op.StackGrowth));
                            SyncHead(delta);
                            CallBody(instruction, op);
                            il.Emit(OpCodes.Pop);
                            delta += op.StackGrowth;
                            break;
                        case IlKind.Native:
                            EmitNative(op, ref delta);
                            break;
                        case IlKind.MLoad:
                            EmitMLoad(segment, i, op, delta);
                            break;
                        case IlKind.MStore:
                            EmitMStore(segment, i, delta);
                            delta -= 2;
                            break;
                        case IlKind.EmbeddedBody:
                        {
                            for (int position = delta - op.StackInputs; position < delta; position++)
                                Materialize(position);
                            int after = delta + op.StackGrowth;
                            // Aliases of the slots the body writes read them first; the hand-back snapshot names where.
                            BeforeWrite(delta - op.StackInputs, Math.Max(delta, after));
                            EmbeddedCall(segment, i, delta, after, Snapshot(after));
                            delta = after;
                            break;
                        }
                        case IlKind.Stop:
                            // The frame ends, so nothing still virtual needs writing out.
                            ExitWith(EvmExceptionType.Stop, instruction.Pc + 1, delta, 0);
                            EndSegment();
                            return;
                        case IlKind.Jump:
                            EmitJump(instruction, delta);
                            EndSegment();
                            return;
                        case IlKind.JumpIf:
                            EmitJumpIf(instruction, delta, fusedPush: PrecededByPush2(segment, i, instruction));
                            EndSegment();
                            return;
                        default:
                            throw new InvalidOperationException($"{op.Kind} in a segment");
                    }
                }

                il.Category = "segment end";
                MaterializeBelow(delta);
                EndSegment();
                MoveHead(delta);
            }

            /// <summary>frame = base + head * 32, the segment's starting slot.</summary>
            private void SetFrame()
            {
                il.Emit(OpCodes.Ldloc, _base);
                il.Emit(OpCodes.Ldloc, _head);
                il.Emit(OpCodes.Ldc_I4_5);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, _frame);
            }

            /// <summary>A value the compiled segment knows without it being in its stack slot yet.</summary>
            private enum VKind : byte
            {
                /// <summary>A word known at compile time.</summary>
                Const,
                /// <summary>A comparison result, 0 or 1, held in an int local.</summary>
                Bool,
                /// <summary>A word held whole in a <c>Vector256&lt;ulong&gt;</c> local (<see cref="IlEvmSettings.VectorForms"/>).</summary>
                Vec,
                /// <summary>What DUP leaves: the word in the slot of position <see cref="VVal.Source"/>, whose own value is there; before that slot is written or takes a new value, the alias reads it into a vector local.</summary>
                Alias,
            }

            /// <summary>An immutable virtual stack value; positions that share one (after DUP) share the object.</summary>
            private sealed class VVal
            {
                public VKind Kind;
                public ulong U0, U1, U2, U3;
                public LocalBuilder? Flag;
                /// <summary>For <see cref="VKind.Bool"/>: the value is <see cref="Flag"/> inverted (ISZERO of it).</summary>
                public bool Negated;

                public LocalBuilder? Local;
                public int Source;

                public static VVal Constant(ulong u0, ulong u1, ulong u2, ulong u3) => new() { Kind = VKind.Const, U0 = u0, U1 = u1, U2 = u2, U3 = u3 };
                public static VVal Boolean(LocalBuilder flag, bool negated) => new() { Kind = VKind.Bool, Flag = flag, Negated = negated };
                public static VVal Vector(LocalBuilder local) => new() { Kind = VKind.Vec, Local = local };
                public static VVal AliasOf(int source) => new() { Kind = VKind.Alias, Source = source };
                public bool IsZero => (U0 | U1 | U2 | U3) == 0;
                public bool FitsOffset => U1 == 0 && U2 == 0 && U3 == 0 && U0 <= uint.MaxValue;
            }

            /// <summary>Stack positions, relative to the segment's starting head, whose values have not been written to their slots.</summary>
            private readonly Dictionary<int, VVal> _virtual = [];
            private readonly Stack<LocalBuilder> _freeFlags = new();
            private readonly List<LocalBuilder> _segmentFlags = [];

            private VVal? Virtual(int position) => _virtual.TryGetValue(position, out VVal? value) ? value : null;

            private LocalBuilder NewFlag()
            {
                LocalBuilder flag = _freeFlags.Count > 0 ? _freeFlags.Pop() : il.DeclareLocal(typeof(int));
                _segmentFlags.Add(flag);
                return flag;
            }

            private void EndSegment()
            {
                _virtual.Clear();
                foreach (LocalBuilder flag in _segmentFlags)
                    _freeFlags.Push(flag);
                _segmentFlags.Clear();
            }

            private void EmitStore(int position, VVal value)
            {
                if (value.Kind == VKind.Const)
                {
                    StoreConstant(position, value.U0, value.U1, value.U2, value.U3);
                    return;
                }

                if (value.Kind is VKind.Vec or VKind.Alias)
                {
                    // One 32-byte store of the local or the aliased slot's word: an alias's slot holds its position's own value, unwritten while the alias lives, so stores go in any order.
                    Slot(position);
                    LoadVec(position, value);
                    il.Emit(OpCodes.Stobj, typeof(Vector256<ulong>));
                    return;
                }

                StoreSmallWord(position, () =>
                {
                    il.Emit(OpCodes.Ldloc, value.Flag!);
                    if (value.Negated)
                    {
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Xor);
                    }
                    il.Emit(OpCodes.Conv_U8);
                });
            }

            private void Materialize(int position)
            {
                if (_virtual.Remove(position, out VVal? value))
                    EmitStore(position, value);
            }

            /// <summary>SWAP: exchanges two positions' values; a word in its slot whose position turns virtual is read into a local first.</summary>
            private void SwapValues(int a, int b)
            {
                VVal? va = Virtual(a);
                VVal? vb = Virtual(b);
                if (va is not null && vb is not null)
                {
                    _virtual[a] = vb;
                    _virtual[b] = va;
                    return;
                }

                if (va is null && vb is null)
                {
                    // Both in their slots: each takes the other's word, read before anything is written.
                    Slot(a);
                    il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                    il.Emit(OpCodes.Stloc, VecScratchB);
                    Slot(b);
                    il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                    il.Emit(OpCodes.Stloc, VecScratch);
                    ResolveAliasesOf(a);
                    ResolveAliasesOf(b);
                    LocalBuilder forA = NewVec();
                    il.Emit(OpCodes.Ldloc, VecScratch);
                    il.Emit(OpCodes.Stloc, forA);
                    _virtual[a] = VVal.Vector(forA);
                    LocalBuilder forB = NewVec();
                    il.Emit(OpCodes.Ldloc, VecScratchB);
                    il.Emit(OpCodes.Stloc, forB);
                    _virtual[b] = VVal.Vector(forB);
                    return;
                }

                // One in its slot: the other position takes its word, read now; it takes the other's value.
                int inSlot = va is null ? a : b;
                int other = va is null ? b : a;
                VVal value = (va ?? vb)!;
                // An alias of that very slot: both hold its word, and nothing changes.
                if (value.Kind == VKind.Alias && value.Source == inSlot)
                    return;
                // The slot's aliases first: a spill while resolving may write the other position out and free its local, so the swap restarts from what is left.
                if (ResolveAliasesOf(inSlot))
                {
                    SwapValues(a, b);
                    return;
                }

                Slot(inSlot);
                il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                il.Emit(OpCodes.Stloc, VecScratch);
                _virtual[inSlot] = value;
                LocalBuilder local = NewVec();
                il.Emit(OpCodes.Ldloc, VecScratch);
                il.Emit(OpCodes.Stloc, local);
                _virtual[other] = VVal.Vector(local);
            }

            /// <summary>Writes out every virtual value below <paramref name="limit"/>, the positions that stay on the stack.</summary>
            private void MaterializeBelow(int limit)
            {
                foreach (int position in _virtual.Keys.Where(position => position < limit).OrderBy(position => position).ToArray())
                    Materialize(position);
            }

            /// <summary>The virtual values below <paramref name="limit"/>, for an out-of-line path that must write them out.</summary>
            private (int Position, VVal Value)[] Snapshot(int limit) =>
                _virtual.Where(entry => entry.Key < limit).OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value)).ToArray();

            /// <summary>Whether this chunk keeps words in vector locals and DUPs as aliases (<see cref="IlEvmSettings.VectorForms"/>).</summary>
            private readonly bool _vstack = IlEvmSettings.VectorForms && IlEvmSettings.DirectArithmetic;

            /// <summary>The chunk's vector locals; one holds a word while some position's <see cref="VKind.Vec"/> value is it.</summary>
            private readonly List<LocalBuilder> _vecLocals = [];

            /// <summary>The most vector locals a chunk declares; past them the words held in locals are written to their slots.</summary>
            private const int MaxVecLocals = 48;

            private LocalBuilder? _vecScratchB;

            /// <summary>A second scratch vector, for an operand an operation reads again after computing into <see cref="VecScratch"/>.</summary>
            private LocalBuilder VecScratchB => _vecScratchB ??= il.DeclareLocal(typeof(Vector256<ulong>));

            /// <summary>A vector local no position's value holds, or null when all are declared; one a cold path's snapshot names may be taken, as the path reads it where it branches off.</summary>
            private LocalBuilder? TryNewVec()
            {
                HashSet<LocalBuilder>? used = null;
                foreach (VVal value in _virtual.Values)
                {
                    if (value.Kind == VKind.Vec)
                        (used ??= []).Add(value.Local!);
                }

                foreach (LocalBuilder local in _vecLocals)
                {
                    if (used is null || !used.Contains(local))
                        return local;
                }

                if (_vecLocals.Count >= MaxVecLocals)
                    return null;
                LocalBuilder created = il.DeclareLocal(typeof(Vector256<ulong>));
                _vecLocals.Add(created);
                return created;
            }

            /// <summary>A vector local for a new value, writing the words held in locals out to their slots if none is free.</summary>
            private LocalBuilder NewVec()
            {
                if (TryNewVec() is { } local)
                    return local;
                SpillVecs();
                return TryNewVec()!;
            }

            private void SpillVecs()
            {
                foreach (int position in _virtual.Where(static e => e.Value.Kind == VKind.Vec).Select(static e => e.Key).Order().ToArray())
                    Materialize(position);
            }

            /// <summary>Before the slot of <paramref name="position"/> is written or takes a new value: its aliases read its word into one vector local.</summary>
            /// <returns>Whether the slot had aliases, and resolving them may have spilled the vector locals.</returns>
            private bool ResolveAliasesOf(int position)
            {
                if (!_vstack)
                    return false;
                List<int>? aliased = null;
                foreach ((int at, VVal value) in _virtual)
                {
                    if (value.Kind == VKind.Alias && value.Source == position)
                        (aliased ??= []).Add(at);
                }

                if (aliased is null)
                    return false;
                LocalBuilder local = NewVec();
                Slot(position);
                il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                il.Emit(OpCodes.Stloc, local);
                VVal word = VVal.Vector(local);
                foreach (int at in aliased)
                    _virtual[at] = word;
                return true;
            }

            /// <summary>Before an operation writes the slots of the positions from <paramref name="from"/> to <paramref name="to"/>, exclusive.</summary>
            private void BeforeWrite(int from, int to)
            {
                for (int position = from; position < to; position++)
                    ResolveAliasesOf(position);
            }

            /// <summary>Gives <paramref name="position"/>, whose old value has been consumed, a value it does not hold in its slot.</summary>
            private void SetValue(int position, VVal value)
            {
                if (!_virtual.ContainsKey(position))
                    ResolveAliasesOf(position);
                _virtual[position] = value;
            }

            /// <summary>Gives <paramref name="position"/>, whose old value has been consumed, the word left in <see cref="VecScratch"/>.</summary>
            private void BindScratch(int position)
            {
                _virtual.Remove(position);
                ResolveAliasesOf(position);
                LocalBuilder local = NewVec();
                il.Emit(OpCodes.Ldloc, VecScratch);
                il.Emit(OpCodes.Stloc, local);
                _virtual[position] = VVal.Vector(local);
            }

            /// <summary>For an operation that reads its operands from their slots: writes a virtual one other than a constant there.</summary>
            private void InSlotUnlessConstant(int position, ref VVal? value)
            {
                if (value is not null && value.Kind != VKind.Const)
                {
                    Materialize(position);
                    value = null;
                }
            }

            private void LoadLimb(int position, VVal? value, int limb)
            {
                switch (value?.Kind)
                {
                    case null:
                        SlotLimb(position, limb);
                        break;
                    case VKind.Alias:
                        SlotLimb(value.Source, limb);
                        break;
                    case VKind.Const:
                        il.Emit(OpCodes.Ldc_I8, (long)(limb switch { 0 => value.U0, 1 => value.U1, 2 => value.U2, _ => value.U3 }));
                        break;
                    case VKind.Vec:
                        il.Emit(OpCodes.Ldloc, value.Local!);
                        il.Emit(OpCodes.Ldc_I4, limb);
                        il.Emit(OpCodes.Call, VecGetElement);
                        break;
                    default:
                        if (limb != 0)
                        {
                            il.Emit(OpCodes.Ldc_I8, 0L);
                            break;
                        }

                        il.Emit(OpCodes.Ldloc, value.Flag!);
                        if (value.Negated)
                        {
                            il.Emit(OpCodes.Ldc_I4_1);
                            il.Emit(OpCodes.Xor);
                        }

                        il.Emit(OpCodes.Conv_U8);
                        break;
                }
            }

            private void EmitLimbs(VVal constant)
            {
                il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                il.Emit(OpCodes.Ldc_I8, (long)constant.U1);
                il.Emit(OpCodes.Ldc_I8, (long)constant.U2);
                il.Emit(OpCodes.Ldc_I8, (long)constant.U3);
            }

            private void CopySlot(int destination, int source)
            {
                Slot(destination);
                Slot(source);
                il.Emit(OpCodes.Ldc_I4, EvmStack.WordSize);
                il.Emit(OpCodes.Cpblk);
            }

            /// <summary>Runs the opcode's operation at compile time, exactly as the interpreter's body runs it.</summary>
            private static VVal Fold(IlOp op, VVal a, VVal? b)
            {
                switch (op.Family)
                {
                    case IlFamily.Math2:
                    case IlFamily.Shift:
                    {
                        object?[] arguments = [new UInt256(a.U0, a.U1, a.U2, a.U3), new UInt256(b!.U0, b.U1, b.U2, b.U3), null];
                        op.Operation!.Invoke(null, arguments);
                        UInt256 result = (UInt256)arguments[2]!;
                        return VVal.Constant(result.u0, result.u1, result.u2, result.u3);
                    }
                    case IlFamily.Bitwise:
                        return FromWord((EvmWord)op.Operation!.Invoke(null, [Word(a), Word(b!)])!);
                    case IlFamily.Math1:
                        return FromWord((EvmWord)op.Operation!.Invoke(null, [Word(a)])!);
                    default:
                        throw new InvalidOperationException($"{op.Name} does not fold");
                }
            }

            private static EvmWord Word(VVal constant) => Vector256.Create(constant.U0, constant.U1, constant.U2, constant.U3).AsByte();

            private static VVal FromWord(EvmWord word)
            {
                Vector256<ulong> limbs = word.AsUInt64();
                return VVal.Constant(limbs.GetElement(0), limbs.GetElement(1), limbs.GetElement(2), limbs.GetElement(3));
            }

            private void EmitNative(IlOp op, ref int delta)
            {
                if (_vstack)
                {
                    EmitNativeTracked(op, ref delta);
                    return;
                }

                if (op.StackInputs == 1)
                {
                    int x = delta - 1;
                    VVal? value = Virtual(x);
                    if (value is { Kind: VKind.Const })
                    {
                        _virtual[x] = Fold(op, value, null);
                        return;
                    }

                    if (op.IsPredicate)
                    {
                        // ISZERO: of a flag, the flag inverted; of a word, a new flag.
                        if (value is { Kind: VKind.Bool })
                        {
                            _virtual[x] = VVal.Boolean(value.Flag!, !value.Negated);
                            return;
                        }

                        LocalBuilder flag = NewFlag();
                        if (IlEvmSettings.DirectArithmetic)
                        {
                            SlotIsZero(x);
                        }
                        else
                        {
                            Slot(x);
                            il.Emit(OpCodes.Call, Arithmetic(op.Predicate!));
                        }

                        il.Emit(OpCodes.Stloc, flag);
                        _virtual[x] = VVal.Boolean(flag, negated: false);
                        return;
                    }

                    Materialize(x);
                    if (IlEvmSettings.DirectArithmetic && Vector256.IsHardwareAccelerated && op.OperationType == typeof(EvmInstructions.OpNot))
                    {
                        // NOT complements the slot in place: a load, a vector xor and a store, no call.
                        Slot(x);
                        Slot(x);
                        il.Emit(OpCodes.Ldobj, typeof(EvmWord));
                        il.Emit(OpCodes.Call, VectorOnesComplement);
                        il.Emit(OpCodes.Stobj, typeof(EvmWord));
                        return;
                    }

                    Slot(x);
                    il.Emit(OpCodes.Call, Arithmetic(op.Native!));
                    return;
                }

                int top = delta - 1;
                int second = delta - 2;
                VVal? va = Virtual(top);
                VVal? vb = Virtual(second);
                if (va is { Kind: VKind.Bool })
                {
                    Materialize(top);
                    va = null;
                }
                if (vb is { Kind: VKind.Bool })
                {
                    Materialize(second);
                    vb = null;
                }

                delta--;
                _virtual.Remove(top);
                if (va is not null && vb is not null)
                {
                    _virtual[second] = Fold(op, va, vb);
                    return;
                }

                if (op.IsPredicate)
                {
                    LocalBuilder flag = NewFlag();
                    if (IlEvmSettings.DirectArithmetic && TryEmitComparison(op, va, vb, top, second))
                    {
                        // The flag is on the evaluation stack.
                    }
                    else if (va is not null)
                    {
                        Slot(second);
                        EmitLimbs(va);
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Call, Arithmetic(op.PredicateConst!));
                    }
                    else if (vb is not null)
                    {
                        Slot(top);
                        EmitLimbs(vb);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Call, Arithmetic(op.PredicateConst!));
                    }
                    else
                    {
                        Slot(top);
                        il.Emit(OpCodes.Call, Arithmetic(op.Predicate!));
                    }
                    il.Emit(OpCodes.Stloc, flag);
                    _virtual[second] = VVal.Boolean(flag, negated: false);
                    return;
                }

                _virtual.Remove(second);
                if (IlEvmSettings.DirectArithmetic && TryEmitWithConstant(op, va, vb, top, second))
                    return;

                if (va is not null)
                {
                    // The constant was on top; the other operand and the result share the second slot.
                    Slot(second);
                    Slot(second);
                    EmitLimbs(va);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Call, Arithmetic(op.NativeConst!));
                }
                else if (vb is not null)
                {
                    Slot(top);
                    Slot(second);
                    EmitLimbs(vb);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, Arithmetic(op.NativeConst!));
                }
                else
                {
                    Slot(top);
                    il.Emit(OpCodes.Call, Arithmetic(op.Native!));
                }
            }

            private static Type? OperationOf(IlOp op) => op.OperationType;

            /// <summary>EQ (any constant), or LT or GT (a constant below 2^64), with one constant operand as a flag on the evaluation stack; a constant on top is the left side.</summary>
            private bool TryEmitComparison(IlOp op, VVal? va, VVal? vb, int top, int second)
            {
                VVal? constant = va ?? vb;
                if (constant is not { Kind: VKind.Const })
                    return false;
                int x = va is not null ? second : top;
                Type? operation = OperationOf(op);
                bool small = constant.U1 == 0 && constant.U2 == 0 && constant.U3 == 0;
                if (operation == typeof(EvmInstructions.OpBitwiseEq))
                {
                    // x == c: every limb equal.
                    if (small)
                    {
                        EmitHighLimbs(x);
                        il.Emit(OpCodes.Ldc_I8, 0L);
                        il.Emit(OpCodes.Ceq);
                    }
                    else
                    {
                        EmitLimbEquals(x, 3, constant.U3);
                        EmitLimbEquals(x, 2, constant.U2);
                        il.Emit(OpCodes.And);
                        EmitLimbEquals(x, 1, constant.U1);
                        il.Emit(OpCodes.And);
                    }

                    EmitLimbEquals(x, 0, constant.U0);
                    il.Emit(OpCodes.And);
                    return true;
                }

                if (!small)
                    return false;

                // LT: top < second; GT: top > second. With the constant on top the comparison is c op x, else x op c.
                bool xBelowC;
                if (operation == typeof(EvmInstructions.OpLt))
                    xBelowC = va is null;
                else if (operation == typeof(EvmInstructions.OpGt))
                    xBelowC = va is not null;
                else
                    return false;

                if (xBelowC)
                {
                    // x < c: no high limbs, and the low limb below c.
                    EmitHighLimbs(x);
                    il.Emit(OpCodes.Ldc_I8, 0L);
                    il.Emit(OpCodes.Ceq);
                    SlotLimb(x, 0);
                    il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                    il.Emit(OpCodes.Clt_Un);
                    il.Emit(OpCodes.And);
                }
                else
                {
                    // x > c: any high limb, or the low limb above c.
                    EmitHighLimbs(x);
                    il.Emit(OpCodes.Ldc_I8, 0L);
                    il.Emit(OpCodes.Cgt_Un);
                    SlotLimb(x, 0);
                    il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                    il.Emit(OpCodes.Cgt_Un);
                    il.Emit(OpCodes.Or);
                }

                return true;
            }

            private void EmitHighLimbs(int slot)
            {
                SlotLimb(slot, 1);
                SlotLimb(slot, 2);
                il.Emit(OpCodes.Or);
                SlotLimb(slot, 3);
                il.Emit(OpCodes.Or);
            }

            private void EmitLimbEquals(int slot, int limb, ulong value)
            {
                SlotLimb(slot, limb);
                il.Emit(OpCodes.Ldc_I8, (long)value);
                il.Emit(OpCodes.Ceq);
            }

            private void LimbAddress(int slot, int limb)
            {
                il.Emit(OpCodes.Ldloc, _frame);
                int offset = slot * EvmStack.WordSize + limb * sizeof(ulong);
                if (offset != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, offset);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Add);
                }
            }

            private void LoadVec(int position, VVal? value)
            {
                if (value is null || value.Kind == VKind.Alias)
                {
                    Slot(value is null ? position : value.Source);
                    il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                }
                else if (value.Kind == VKind.Vec)
                {
                    il.Emit(OpCodes.Ldloc, value.Local!);
                }
                else if (value.Kind == VKind.Const)
                {
                    EmitVecConst(value.U0, value.U1, value.U2, value.U3);
                }
                else
                {
                    il.Emit(OpCodes.Ldloc, value.Flag!);
                    if (value.Negated)
                    {
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Xor);
                    }

                    il.Emit(OpCodes.Conv_U8);
                    il.Emit(OpCodes.Call, VectorCreateScalar);
                }
            }

            /// <summary>Pushes a constant word as a vector, which the JIT loads from its data.</summary>
            private void EmitVecConst(ulong u0, ulong u1, ulong u2, ulong u3)
            {
                if ((u0 | u1 | u2 | u3) == 0)
                {
                    il.Emit(OpCodes.Call, VecZero);
                    return;
                }

                il.Emit(OpCodes.Ldc_I8, (long)u0);
                if ((u1 | u2 | u3) == 0)
                {
                    il.Emit(OpCodes.Call, VectorCreateScalar);
                    return;
                }

                il.Emit(OpCodes.Ldc_I8, (long)u1);
                il.Emit(OpCodes.Ldc_I8, (long)u2);
                il.Emit(OpCodes.Ldc_I8, (long)u3);
                il.Emit(OpCodes.Call, VectorCreate);
            }

            private LocalBuilder? _vecScratch;
            private LocalBuilder? _signedScratch0;
            private LocalBuilder? _signedScratch1;

            private LocalBuilder VecScratch => _vecScratch ??= il.DeclareLocal(typeof(Vector256<ulong>));

            /// <summary>The bit flipping a limb's sign, so that unsigned limbs compare as signed ones.</summary>
            private const ulong SignBit = 0x8000_0000_0000_0000;

            /// <summary>EQ of any two words, and LT, GT, SLT and SGT of any but a small constant and a word, as a flag from whole-word vector reads.</summary>
            /// <remarks>The most significant differing limb decides: with sign bits flipped (but the top limb's for signed forms), signed limb compares give 4-bit below and above masks, and the first word is below when its mask is larger.</remarks>
            private bool TryEmitVectorComparison(IlOp op, VVal? va, VVal? vb, int top, int second)
            {
                Type? operation = OperationOf(op);
                if (operation == typeof(EvmInstructions.OpBitwiseEq))
                {
                    LoadVec(top, va);
                    LoadVec(second, vb);
                    il.Emit(OpCodes.Call, VecEqualsAll);
                    return true;
                }

                bool below = operation == typeof(EvmInstructions.OpLt) || operation == typeof(EvmInstructions.OpSLt);
                bool signed = operation == typeof(EvmInstructions.OpSLt) || operation == typeof(EvmInstructions.OpSGt);
                if (!below && !signed && operation != typeof(EvmInstructions.OpGt))
                    return false;

                // A small constant against a word in its slot reads three limbs and compares one, as cheap as this.
                VVal? constant = va is { Kind: VKind.Const } ? va : vb is { Kind: VKind.Const } ? vb : null;
                VVal? word = ReferenceEquals(constant, va) ? vb : va;
                if (!signed && constant is not null && word is null && constant.U1 == 0 && constant.U2 == 0 && constant.U3 == 0)
                    return false;

                ulong topFlip = signed ? 0 : SignBit;
                _signedScratch0 ??= il.DeclareLocal(typeof(Vector256<long>));
                _signedScratch1 ??= il.DeclareLocal(typeof(Vector256<long>));
                LoadFlipped(top, va, topFlip);
                il.Emit(OpCodes.Stloc, _signedScratch0);
                LoadFlipped(second, vb, topFlip);
                il.Emit(OpCodes.Stloc, _signedScratch1);
                // The opcode compares the top (a) with the second (b): LT and SLT ask a < b, GT and SGT a > b.
                LocalBuilder low = below ? _signedScratch0 : _signedScratch1;
                LocalBuilder high = below ? _signedScratch1 : _signedScratch0;
                // Limbs where low < high, then where low > high; low < high when the first mask is the larger.
                il.Emit(OpCodes.Ldloc, high);
                il.Emit(OpCodes.Ldloc, low);
                il.Emit(OpCodes.Call, VecGreaterThanSigned);
                il.Emit(OpCodes.Call, VecMaskSigned);
                il.Emit(OpCodes.Ldloc, low);
                il.Emit(OpCodes.Ldloc, high);
                il.Emit(OpCodes.Call, VecGreaterThanSigned);
                il.Emit(OpCodes.Call, VecMaskSigned);
                il.Emit(OpCodes.Cgt_Un);
                return true;
            }

            /// <summary>Pushes the word at <paramref name="position"/> with each limb's sign bit flipped, the top limb's only by <paramref name="topFlip"/>, as signed limbs.</summary>
            private void LoadFlipped(int position, VVal? value, ulong topFlip)
            {
                if (value is { Kind: VKind.Const })
                {
                    EmitVecConst(value.U0 ^ SignBit, value.U1 ^ SignBit, value.U2 ^ SignBit, value.U3 ^ topFlip);
                }
                else
                {
                    LoadVec(position, value);
                    EmitVecConst(SignBit, SignBit, SignBit, topFlip);
                    il.Emit(OpCodes.Call, VecXor);
                }

                il.Emit(OpCodes.Call, VecAsInt64);
            }

            /// <summary>An arithmetic, comparison or bitwise opcode over tracked values: vector forms read operands wherever they are, helpers from the slots, constants aside.</summary>
            private void EmitNativeTracked(IlOp op, ref int delta)
            {
                if (op.StackInputs == 1)
                {
                    int x = delta - 1;
                    VVal? value = Virtual(x);
                    if (value is { Kind: VKind.Const })
                    {
                        _virtual[x] = Fold(op, value, null);
                        return;
                    }

                    if (op.IsPredicate)
                    {
                        // ISZERO: of a flag, the flag inverted; of a word, a new flag from one whole-word test.
                        if (value is { Kind: VKind.Bool })
                        {
                            _virtual[x] = VVal.Boolean(value.Flag!, !value.Negated);
                            return;
                        }

                        LocalBuilder flag = NewFlag();
                        LoadVec(x, value);
                        il.Emit(OpCodes.Call, VecZero);
                        il.Emit(OpCodes.Call, VecEqualsAll);
                        il.Emit(OpCodes.Stloc, flag);
                        _virtual.Remove(x);
                        SetValue(x, VVal.Boolean(flag, negated: false));
                        return;
                    }

                    if (op.OperationType == typeof(EvmInstructions.OpNot))
                    {
                        LoadVec(x, value);
                        il.Emit(OpCodes.Call, VecOnesComplement);
                        il.Emit(OpCodes.Stloc, VecScratch);
                        BindScratch(x);
                        return;
                    }

                    // CLZ and the like: the helper over the slot, in place.
                    InSlotUnlessConstant(x, ref value);
                    BeforeWrite(x, x + 1);
                    Slot(x);
                    il.Emit(OpCodes.Call, Arithmetic(op.Native!));
                    return;
                }

                int top = delta - 1;
                int second = delta - 2;
                VVal? va = Virtual(top);
                VVal? vb = Virtual(second);
                delta--;
                if (va is { Kind: VKind.Const } && vb is { Kind: VKind.Const })
                {
                    _virtual.Remove(top);
                    _virtual[second] = Fold(op, va, vb);
                    return;
                }

                if (op.IsPredicate)
                {
                    LocalBuilder flag = NewFlag();
                    if (!TryEmitVectorComparison(op, va, vb, top, second))
                    {
                        // Not a vector form: the limb compare and the helpers read operands from their slots.
                        InSlotUnlessConstant(top, ref va);
                        InSlotUnlessConstant(second, ref vb);
                        if (TryEmitComparison(op, va, vb, top, second))
                        {
                            // The flag is on the evaluation stack.
                        }
                        else if (va is not null)
                        {
                            Slot(second);
                            EmitLimbs(va);
                            il.Emit(OpCodes.Ldc_I4_1);
                            il.Emit(OpCodes.Call, Arithmetic(op.PredicateConst!));
                        }
                        else if (vb is not null)
                        {
                            Slot(top);
                            EmitLimbs(vb);
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Call, Arithmetic(op.PredicateConst!));
                        }
                        else
                        {
                            Slot(top);
                            il.Emit(OpCodes.Call, Arithmetic(op.Predicate!));
                        }
                    }

                    il.Emit(OpCodes.Stloc, flag);
                    _virtual.Remove(top);
                    _virtual.Remove(second);
                    SetValue(second, VVal.Boolean(flag, negated: false));
                    return;
                }

                if (TryEmitVectorForm(op, va, vb, top, second))
                {
                    _virtual.Remove(top);
                    BindScratch(second);
                    return;
                }

                // The helpers read both operands from their slots, a constant aside, and write the second slot.
                InSlotUnlessConstant(top, ref va);
                InSlotUnlessConstant(second, ref vb);
                _virtual.Remove(top);
                _virtual.Remove(second);
                BeforeWrite(second, second + 1);
                if (va is not null)
                {
                    Slot(second);
                    Slot(second);
                    EmitLimbs(va);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Call, Arithmetic(op.NativeConst!));
                }
                else if (vb is not null)
                {
                    Slot(top);
                    Slot(second);
                    EmitLimbs(vb);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, Arithmetic(op.NativeConst!));
                }
                else
                {
                    Slot(top);
                    il.Emit(OpCodes.Call, Arithmetic(op.Native!));
                }
            }

            /// <summary>AND, OR, XOR; SHL and SHR by a constant; ADD and SUB of a constant below 2^64, or whose two's complement is: the result left in <see cref="VecScratch"/>.</summary>
            private bool TryEmitVectorForm(IlOp op, VVal? va, VVal? vb, int top, int second)
            {
                Type? operation = OperationOf(op);
                MethodInfo? bitwise = operation == typeof(EvmInstructions.OpBitwiseAnd) ? VecAnd
                    : operation == typeof(EvmInstructions.OpBitwiseOr) ? VecOr
                    : operation == typeof(EvmInstructions.OpBitwiseXor) ? VecXor
                    : null;
                if (bitwise is not null)
                {
                    LoadVec(top, va);
                    LoadVec(second, vb);
                    il.Emit(OpCodes.Call, bitwise);
                    il.Emit(OpCodes.Stloc, VecScratch);
                    return true;
                }

                // Two-word arithmetic stays with the helpers: a vector ADD and SUB carrying between limbs measured slower.
                VVal? constant = va is { Kind: VKind.Const } ? va : vb is { Kind: VKind.Const } ? vb : null;
                if (constant is null)
                    return false;

                bool constantOnTop = ReferenceEquals(constant, va);
                int x = constantOnTop ? second : top;
                VVal? vx = constantOnTop ? vb : va;

                // The core's shift operations are internal, so they are told apart by name.
                if (operation?.Name is "OpShr" or "OpShl")
                {
                    // Only a constant amount, on top, shifting the second.
                    if (!constantOnTop)
                        return false;
                    if (constant.U1 != 0 || constant.U2 != 0 || constant.U3 != 0 || constant.U0 >= 256)
                    {
                        il.Emit(OpCodes.Call, VecZero);
                        il.Emit(OpCodes.Stloc, VecScratch);
                        return true;
                    }

                    EmitShiftConstant(second, vb, (int)constant.U0, right: operation.Name == "OpShr");
                    il.Emit(OpCodes.Stloc, VecScratch);
                    return true;
                }

                // MUL by a power of two is a left shift, DIV by one a right shift, MOD by one a mask; by zero each is zero.
                bool mul = operation == typeof(EvmInstructions.OpMul);
                if (mul || ((operation == typeof(EvmInstructions.OpDiv) || operation == typeof(EvmInstructions.OpMod)) && !constantOnTop))
                {
                    if (constant.IsZero)
                    {
                        il.Emit(OpCodes.Call, VecZero);
                        il.Emit(OpCodes.Stloc, VecScratch);
                        return true;
                    }

                    int bit = PowerOfTwo(constant);
                    if (bit < 0)
                        return false;
                    if (operation == typeof(EvmInstructions.OpMod))
                    {
                        LoadVec(x, vx);
                        EmitVecConst(LowBits(bit, 0), LowBits(bit, 1), LowBits(bit, 2), LowBits(bit, 3));
                        il.Emit(OpCodes.Call, VecAnd);
                    }
                    else
                    {
                        EmitShiftConstant(x, vx, bit, right: !mul);
                    }

                    il.Emit(OpCodes.Stloc, VecScratch);
                    return true;
                }

                bool add = operation == typeof(EvmInstructions.OpAdd);
                if (!add && operation != typeof(EvmInstructions.OpSub))
                    return false;
                if (!add && constantOnTop)
                    return false;

                // x + c or x - c; a constant whose two's complement is small adds or subtracts that instead.
                ulong amount;
                bool subtract = !add;
                if (constant.U1 == 0 && constant.U2 == 0 && constant.U3 == 0)
                {
                    amount = constant.U0;
                }
                else if (constant.U1 == ulong.MaxValue && constant.U2 == ulong.MaxValue && constant.U3 == ulong.MaxValue && constant.U0 != 0)
                {
                    amount = 0 - constant.U0;
                    subtract = !subtract;
                }
                else
                {
                    return false;
                }

                LoadVec(x, vx);
                if (amount == 0)
                {
                    il.Emit(OpCodes.Stloc, VecScratch);
                    return true;
                }

                // The low limb changes in the vector; when it wraps, the limbs above take the carry or borrow out of line.
                il.Emit(OpCodes.Stloc, VecScratchB);
                il.Emit(OpCodes.Ldloc, VecScratchB);
                EmitVecConst(amount, 0, 0, 0);
                il.Emit(OpCodes.Call, subtract ? VecSubtract : VecAdd);
                il.Emit(OpCodes.Stloc, VecScratch);
                Label wrapped = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldloc, VecScratchB);
                il.Emit(OpCodes.Call, VecToScalar);
                if (!subtract)
                {
                    // Added: wrapped when the new low limb is below the amount. Subtracted: when the old one was.
                    il.Emit(OpCodes.Ldc_I8, (long)amount);
                    il.Emit(OpCodes.Add);
                }

                il.Emit(OpCodes.Ldc_I8, (long)amount);
                il.Emit(OpCodes.Blt_Un, wrapped);
                il.MarkLabel(back);
                AddOutOfLine(() =>
                {
                    il.MarkLabel(wrapped);
                    il.Emit(OpCodes.Ldloc, VecScratch);
                    il.Emit(OpCodes.Call, !subtract ? CarryVectorMethod : BorrowVectorMethod);
                    il.Emit(OpCodes.Stloc, VecScratch);
                    il.Emit(OpCodes.Br, back);
                });
                return true;
            }

            /// <summary>AND and OR with a constant, SHL and SHR by a constant, ADD of a constant below 2^64 and SUB of one from the top, written into the second slot.</summary>
            private bool TryEmitWithConstant(IlOp op, VVal? va, VVal? vb, int top, int second)
            {
                VVal? constant = va ?? vb;
                if (constant is not { Kind: VKind.Const })
                    return false;
                int x = va is not null ? second : top;
                Type? operation = OperationOf(op);
                ulong[] c = [constant.U0, constant.U1, constant.U2, constant.U3];
                if (operation == typeof(EvmInstructions.OpBitwiseAnd) || operation == typeof(EvmInstructions.OpBitwiseOr))
                {
                    bool and = operation == typeof(EvmInstructions.OpBitwiseAnd);
                    for (int limb = 0; limb < 4; limb++)
                    {
                        ulong keep = and ? ulong.MaxValue : 0;
                        ulong fixedValue = and ? 0 : ulong.MaxValue;
                        if (c[limb] == fixedValue)
                        {
                            StoreLimb(second, limb, fixedValue);
                        }
                        else if (c[limb] == keep)
                        {
                            if (x != second)
                            {
                                LimbAddress(second, limb);
                                SlotLimb(x, limb);
                                il.Emit(OpCodes.Stind_I8);
                            }
                        }
                        else
                        {
                            LimbAddress(second, limb);
                            SlotLimb(x, limb);
                            il.Emit(OpCodes.Ldc_I8, (long)c[limb]);
                            il.Emit(and ? OpCodes.And : OpCodes.Or);
                            il.Emit(OpCodes.Stind_I8);
                        }
                    }

                    return true;
                }

                if (operation?.Name is "OpShr" or "OpShl")
                {
                    // Only a constant amount, on top, with the value in the second slot shifted in place.
                    if (va is null)
                        return false;
                    if (constant.U1 != 0 || constant.U2 != 0 || constant.U3 != 0 || constant.U0 >= 256)
                    {
                        StoreConstant(second, 0, 0, 0, 0);
                        return true;
                    }

                    int shift = (int)constant.U0;
                    int q = shift / 64;
                    int r = shift % 64;
                    bool right = operation.Name == "OpShr";
                    // Right shifts read limbs at or above the one written, so go up; left shifts go down.
                    for (int step = 0; step < 4; step++)
                    {
                        int limb = right ? step : 3 - step;
                        int source = right ? limb + q : limb - q;
                        if (source < 0 || source > 3)
                        {
                            StoreLimb(second, limb, 0);
                            continue;
                        }

                        if (q == 0 && r == 0)
                            continue;
                        LimbAddress(second, limb);
                        SlotLimb(second, source);
                        if (r != 0)
                        {
                            il.Emit(OpCodes.Ldc_I4, r);
                            il.Emit(right ? OpCodes.Shr_Un : OpCodes.Shl);
                            int carrySource = right ? source + 1 : source - 1;
                            if (carrySource is >= 0 and <= 3)
                            {
                                SlotLimb(second, carrySource);
                                il.Emit(OpCodes.Ldc_I4, 64 - r);
                                il.Emit(right ? OpCodes.Shl : OpCodes.Shr_Un);
                                il.Emit(OpCodes.Or);
                            }
                        }

                        il.Emit(OpCodes.Stind_I8);
                    }

                    return true;
                }

                bool add = operation == typeof(EvmInstructions.OpAdd);
                if ((add || (operation == typeof(EvmInstructions.OpSub) && vb is not null)) &&
                    constant.U1 == 0 && constant.U2 == 0 && constant.U3 == 0)
                {
                    // x + c or x - c in the low limb, carrying into the rest only when it wraps.
                    if (x != second)
                        CopySlot(second, x);
                    if (constant.U0 == 0)
                        return true;
                    Label wrapped = NewLabel();
                    Label back = NewLabel();
                    LimbAddress(second, 0);
                    SlotLimb(second, 0);
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Stloc, _limb);
                    il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                    il.Emit(add ? OpCodes.Add : OpCodes.Sub);
                    il.Emit(OpCodes.Stind_I8);
                    // Added: wrapped when the new low limb is below c. Subtracted: when the old one was below c.
                    if (add)
                        SlotLimb(second, 0);
                    else
                        il.Emit(OpCodes.Ldloc, _limb);
                    il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                    il.Emit(OpCodes.Blt_Un, wrapped);
                    il.MarkLabel(back);
                    AddOutOfLine(() =>
                    {
                        il.MarkLabel(wrapped);
                        Slot(second);
                        il.Emit(OpCodes.Call, add ? CarryMethod : BorrowMethod);
                        il.Emit(OpCodes.Br, back);
                    });
                    return true;
                }

                return false;
            }

            private LocalBuilder? _offsetScratch;

            /// <summary>The offset a CALLDATALOAD reads at, which its cold path reads again; <see cref="VecScratch"/> takes its result.</summary>
            private LocalBuilder OffsetScratch => _offsetScratch ??= il.DeclareLocal(typeof(Vector256<ulong>));

            /// <summary>Byte-reverses the vector on the evaluation stack, between the stack's limb order and memory's big-endian bytes.</summary>
            private void EmitByteSwap()
            {
                // The 128-bit halves swapped, then each half's bytes reversed.
                il.Emit(OpCodes.Ldc_I4, 0b01_00_11_10);
                il.Emit(OpCodes.Call, Permute4x64);
                il.Emit(OpCodes.Call, VecAsByte);
                EmitVecConst(0x18191a1b1c1d1e1fUL, 0x1011121314151617UL, 0x08090a0b0c0d0e0fUL, 0x0001020304050607UL);
                il.Emit(OpCodes.Call, VecAsByte);
                il.Emit(OpCodes.Call, ShuffleBytes);
                il.Emit(OpCodes.Call, VecBytesAsUInt64);
            }

            /// <summary>Branches to <paramref name="far"/> unless the word in <paramref name="local"/> is an offset below 2^32.</summary>
            private void BranchUnlessOffset(LocalBuilder local, Label far)
            {
                il.Emit(OpCodes.Ldloc, local);
                EmitVecConst(0xFFFF_FFFF_0000_0000UL, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
                il.Emit(OpCodes.Call, TestZ);
                il.Emit(OpCodes.Brfalse, far);
            }

            /// <summary>CALLDATALOAD of a tracked offset: 32 bytes inside the input read into a local; anything else through a helper.</summary>
            private void EmitCallDataLoadTracked(int delta)
            {
                int offset = delta - 1;
                VVal? value = Virtual(offset);
                LocalBuilder at = OffsetScratch;
                Label slow = NewLabel();
                Label back = NewLabel();
                LoadVec(offset, value);
                il.Emit(OpCodes.Stloc, at);
                BranchUnlessOffset(at, slow);
                // offset + 32 <= the input's length, then the 32 bytes there.
                il.Emit(OpCodes.Ldloc, at);
                il.Emit(OpCodes.Call, VecToScalar);
                il.Emit(OpCodes.Ldc_I8, 32L);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Call, StackInputDataLength);
                il.Emit(OpCodes.Conv_U8);
                il.Emit(OpCodes.Bgt_Un, slow);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Call, StackInputData);
                il.Emit(OpCodes.Ldloc, at);
                il.Emit(OpCodes.Call, VecToScalar);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Unaligned, (byte)1);
                il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                EmitByteSwap();
                il.Emit(OpCodes.Stloc, VecScratch);
                il.MarkLabel(back);
                BindScratch(offset);
                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    il.Emit(OpCodes.Ldarg_S, ArgStack);
                    il.Emit(OpCodes.Call, StackInputData);
                    il.Emit(OpCodes.Ldarg_S, ArgStack);
                    il.Emit(OpCodes.Call, StackInputDataLength);
                    il.Emit(OpCodes.Ldloc, at);
                    il.Emit(OpCodes.Call, CallDataWordMethod);
                    il.Emit(OpCodes.Stloc, VecScratch);
                    il.Emit(OpCodes.Br, back);
                });
            }

            /// <summary>Pushes the word at <paramref name="position"/> shifted by a constant amount below 256, as one vector.</summary>
            private void EmitShiftConstant(int position, VVal? value, int shift, bool right)
            {
                if (shift == 0)
                {
                    LoadVec(position, value);
                    return;
                }

                int q = shift / 64;
                int r = shift % 64;
                for (int limb = 0; limb < 4; limb++)
                {
                    int source = right ? limb + q : limb - q;
                    if (source < 0 || source > 3)
                    {
                        il.Emit(OpCodes.Ldc_I8, 0L);
                        continue;
                    }

                    LoadLimb(position, value, source);
                    if (r == 0)
                        continue;
                    il.Emit(OpCodes.Ldc_I4, r);
                    il.Emit(right ? OpCodes.Shr_Un : OpCodes.Shl);
                    int carrySource = right ? source + 1 : source - 1;
                    if (carrySource is >= 0 and <= 3)
                    {
                        LoadLimb(position, value, carrySource);
                        il.Emit(OpCodes.Ldc_I4, 64 - r);
                        il.Emit(right ? OpCodes.Shl : OpCodes.Shr_Un);
                        il.Emit(OpCodes.Or);
                    }
                }

                il.Emit(OpCodes.Call, VectorCreate);
            }

            /// <summary>Limb <paramref name="limb"/> of 2^<paramref name="bit"/> - 1.</summary>
            private static ulong LowBits(int bit, int limb) =>
                bit >= 64 * (limb + 1) ? ulong.MaxValue : bit <= 64 * limb ? 0 : (1UL << (bit - 64 * limb)) - 1;

            /// <summary>The bit a power-of-two constant has set, or -1 when it has none or more than one.</summary>
            private static int PowerOfTwo(VVal constant)
            {
                ulong[] limbs = [constant.U0, constant.U1, constant.U2, constant.U3];
                int bit = -1;
                for (int limb = 0; limb < 4; limb++)
                {
                    if (limbs[limb] == 0)
                        continue;
                    if (bit >= 0 || (limbs[limb] & (limbs[limb] - 1)) != 0)
                        return -1;
                    bit = limb * 64 + System.Numerics.BitOperations.TrailingZeroCount(limbs[limb]);
                }

                return bit;
            }

            private void EmitMLoad(IlUnit segment, int index, IlOp op, int delta)
            {
                int offset = delta - 1;
                VVal? value = Virtual(offset);
                if (value is not null && (value.Kind != VKind.Const || !value.FitsOffset))
                {
                    Materialize(offset);
                    value = null;
                }

                // The word loaded lands in the offset's slot.
                BeforeWrite(offset, offset + 1);

                Label slow = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldloc, _memory!);
                if (value is not null)
                {
                    il.Emit(OpCodes.Ldc_I8, (long)value.U0);
                    Slot(offset);
                    il.Emit(OpCodes.Call, Memory(TryMLoadAt));
                }
                else
                {
                    Slot(offset);
                    il.Emit(OpCodes.Call, Memory(TryMLoad));
                }
                il.Emit(OpCodes.Brfalse, slow);
                il.MarkLabel(back);

                // The loaded word is in the slot either way.
                _virtual.Remove(offset);
                (int Position, VVal Value)[] live = Snapshot(delta);
                VVal? constantOffset = value;
                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    if (constantOffset is not null)
                        EmitStore(offset, constantOffset);
                    EmbeddedCall(segment, index, delta, delta + op.StackGrowth, live);
                    il.Emit(OpCodes.Br, back);
                });
            }

            private void EmitMStore(IlUnit segment, int index, int delta)
            {
                int offset = delta - 1;
                int word = delta - 2;
                // The slow path's body byte-swaps the word's slot in place: its aliases read it first, before the operands are looked at, as a spill may write them.
                BeforeWrite(word, offset + 1);
                VVal? vo = Virtual(offset);
                VVal? vw = Virtual(word);
                if (vw is not null && vw.Kind != VKind.Const)
                {
                    Materialize(word);
                    vw = null;
                }
                if (vo is not null && (vo.Kind != VKind.Const || !vo.FitsOffset))
                {
                    Materialize(offset);
                    vo = null;
                }
                if (vo is null && vw is not null)
                {
                    Materialize(word);
                    vw = null;
                }

                Label slow = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldloc, _memory!);
                if (vo is not null && vw is not null)
                {
                    // The word in memory byte order: big-endian limbs, most significant first.
                    il.Emit(OpCodes.Ldc_I8, (long)vo.U0);
                    il.Emit(OpCodes.Ldc_I8, (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(vw.U3));
                    il.Emit(OpCodes.Ldc_I8, (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(vw.U2));
                    il.Emit(OpCodes.Ldc_I8, (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(vw.U1));
                    il.Emit(OpCodes.Ldc_I8, (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(vw.U0));
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Call, Memory(TryMStoreWordAt));
                }
                else if (vo is not null)
                {
                    il.Emit(OpCodes.Ldc_I8, (long)vo.U0);
                    Slot(word);
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Call, Memory(TryMStoreAt));
                }
                else
                {
                    Slot(offset);
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Call, Memory(TryMStore));
                }
                il.Emit(OpCodes.Stloc, _limb);
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Ldc_I8, -1L);
                il.Emit(OpCodes.Beq, slow);
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Stloc, _gas);
                il.MarkLabel(back);

                _virtual.Remove(offset);
                _virtual.Remove(word);
                (int Position, VVal Value)[] live = Snapshot(delta - 2);
                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    if (vo is not null)
                        EmitStore(offset, vo);
                    if (vw is not null)
                        EmitStore(word, vw);
                    EmbeddedCall(segment, index, delta, delta - 2, live);
                    il.Emit(OpCodes.Br, back);
                });
            }

            /// <summary>Runs the body of the segment's <paramref name="index"/>th opcode exactly as the dispatch loop would, gas and faults as in <c>IlEmbedded</c>; a hand-over first writes out <paramref name="live"/>.</summary>
            private void EmbeddedCall(IlUnit segment, int index, int deltaBefore, int deltaAfter, (int Position, VVal Value)[] live)
            {
                IlInstruction instruction = segment.Instructions[index];
                IlOp op = instruction.Op;
                int site = sites.Count;
                sites.Add(new IlSite(instruction.Pc, instruction.Pc + instruction.Size, instruction.GasAfter,
                    segment.FirstTableIndex + index + 1, segment.Instructions.Count - index - 1, op.ExecutePointer,
                    deltaBefore, instruction.GasAfter + op.StaticGas));

                // The head and gas go in as they are; the site's table entry adjusts them, which keeps each site small.
                Label leaves = NewLabel();
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldc_I4, site);
                il.Emit(OpCodes.Ldarg_S, ArgVm);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldloca, _opsAdjust);
                il.Emit(OpCodes.Ldloc, _head);
                il.Emit(OpCodes.Ldloc, _gas);
                il.Emit(OpCodes.Call, EmbeddedMethod);
                il.Emit(OpCodes.Stloc, _result);
                il.Emit(OpCodes.Ldloc, _result);
                il.Emit(OpCodes.Brtrue, leaves);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldfld, GasValue);
                il.Emit(OpCodes.Stloc, _gas);

                _usesLeaveSite = true;
                AddOutOfLine(() =>
                {
                    // The run ends at the body: a hand-over to the interpreter also needs the values still virtual.
                    il.MarkLabel(leaves);
                    if (live.Length > 0)
                    {
                        Label halts = NewLabel();
                        il.Emit(OpCodes.Ldloc, _result);
                        il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                        il.Emit(OpCodes.Bne_Un, halts);
                        foreach ((int position, VVal value) in live)
                            EmitStore(position, value);
                        il.MarkLabel(halts);
                    }

                    il.Emit(OpCodes.Br, _leaveSite);
                });
            }

            private void EmitSingle(IlUnit unit)
            {
                il.Category = IlEvmDiagnostics.Markers ? "single " + (Instruction)unit.Instructions[0].Opcode : "single " + unit.Instructions[0].Op.Kind;
                il.Pc = unit.StartPc;
                IlInstruction instruction = unit.Instructions[0];
                IlOp op = instruction.Op;
                il.MarkLabel(LabelOf(unit));

                if (op.Kind == IlKind.Unsupported)
                {
                    Handback(instruction.Pc, 0, 0, uncount: 0);
                    return;
                }

                SyncHead(0);
                SyncGas(0);
                CallBody(instruction, op);
                il.Emit(OpCodes.Stloc, _result);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldfld, GasValue);
                il.Emit(OpCodes.Stloc, _gas);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldfld, StackHead);
                il.Emit(OpCodes.Stloc, _head);
                AddOps(1);

                if (op.Kind == IlKind.TerminatingBody)
                {
                    StoreProgramCounterFromLocal();
                    il.Emit(OpCodes.Br, _exit);
                    return;
                }

                Label halted = NewLabel();
                Label moved = NewLabel();
                il.Emit(OpCodes.Ldloc, _result);
                il.Emit(OpCodes.Brtrue, halted);
                if (instruction.MayFuseNext && allUnits.TryGetValue(instruction.Pc + instruction.Size, out IlUnit? fused))
                {
                    // The body ran the instruction after it: go on past that one.
                    int pastFused = fused.StartPc + fused.Instructions[0].Size;
                    Label notFused = NewLabel();
                    il.Emit(OpCodes.Ldloc, _pc);
                    il.Emit(OpCodes.Ldc_I4, pastFused);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Bne_Un, notFused);
                    if (allUnits.TryGetValue(pastFused, out IlUnit? after))
                        GoTo(after);
                    else
                        ExitWith(EvmExceptionType.None, codeLength, 0, 0);
                    il.MarkLabel(notFused);
                }
                // A body that moved the counter somewhere else than past itself leaves the frame to the interpreter.
                il.Emit(OpCodes.Ldloc, _pc);
                il.Emit(OpCodes.Ldc_I4, instruction.Pc + instruction.Size);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Bne_Un, moved);

                AddOutOfLine(() =>
                {
                    il.MarkLabel(halted);
                    StoreProgramCounterFromLocal();
                    il.Emit(OpCodes.Br, _exit);

                    il.MarkLabel(moved);
                    StoreProgramCounterFromLocal();
                    il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                    il.Emit(OpCodes.Stloc, _result);
                    il.Emit(OpCodes.Br, _exit);
                });
            }

            /// <summary>Whether a constant names a JUMPDEST of the code, and which unit starts there.</summary>
            private bool TryTarget(VVal destination, [NotNullWhen(true)] out IlUnit? target)
            {
                target = null;
                return destination.Kind == VKind.Const && destination.U1 == 0 && destination.U2 == 0 && destination.U3 == 0 &&
                    destination.U0 < (ulong)codeLength && allUnits.TryGetValue((int)destination.U0, out target) && target.IsJumpDest;
            }

            private void EmitJump(IlInstruction jump, int delta)
            {
                int destination = delta - 1;
                VVal? vd = Virtual(destination);
                _virtual.Remove(destination);
                if (vd is { Kind: VKind.Const })
                {
                    if (TryTarget(vd, out IlUnit? target))
                    {
                        MaterializeBelow(destination);
                        MoveHead(delta - 1);
                        Poll(landedJumpDest: 1, target);
                        GoTo(target);
                    }
                    else
                    {
                        ExitWith(EvmExceptionType.InvalidJumpDestination, jump.Pc + 1, delta - 1, 0);
                    }

                    return;
                }

                if (vd is not null)
                    EmitStore(destination, vd);
                MaterializeBelow(destination);
                Label invalid = NewLabel();
                Label foreign = NewLabel();
                LoadTarget(destination, invalid, foreign);
                MoveHead(delta - 1);
                Poll(landedJumpDest: 1, target: null);
                il.Emit(OpCodes.Br, _dynamicJump);

                AddOutOfLine(() =>
                {
                    il.MarkLabel(invalid);
                    ExitWith(EvmExceptionType.InvalidJumpDestination, jump.Pc + 1, delta - 1, 0);

                    il.MarkLabel(foreign);
                    MoveHead(delta - 1);
                    Poll(landedJumpDest: 1, target: null);
                    ExitContinueAtLimb();
                });
            }

            /// <summary>Whether the instruction before <paramref name="instruction"/> is a PUSH2, which the dispatch loop runs with a following JUMPI as one: its count of a taken JUMPI then holds the landed JUMPDEST, as a JUMP's does.</summary>
            private bool PrecededByPush2(IlUnit segment, int index, IlInstruction instruction)
            {
                if (index > 0)
                    return segment.Instructions[index - 1].Opcode == (byte)Instruction.PUSH2;
                // First in its unit: no jump lands on a JUMPI, so the instruction before it ran just before it.
                foreach (IlUnit unit in allUnits.Values)
                {
                    if (unit.Instructions.Count > 0 && unit.Instructions[^1] is { } last && last.Pc + last.Size == instruction.Pc)
                        return last.Opcode == (byte)Instruction.PUSH2;
                }

                return false;
            }

            /// <param name="fusedPush">Whether a PUSH2 comes just before, so the dispatch loop runs the two as one (<see cref="PrecededByPush2"/>).</param>
            private void EmitJumpIf(IlInstruction jumpIf, int delta, bool fusedPush)
            {
                int destination = delta - 1;
                int condition = delta - 2;
                VVal? vd = Virtual(destination);
                VVal? vc = Virtual(condition);
                _virtual.Remove(destination);
                _virtual.Remove(condition);
                if (vd is not null && vd.Kind != VKind.Const)
                {
                    EmitStore(destination, vd);
                    vd = null;
                }

                // Everything under the condition stays on the stack on both paths.
                MaterializeBelow(condition);

                if (vc is { Kind: VKind.Const })
                {
                    if (!vc.IsZero)
                        EmitTaken(jumpIf, vd, destination, delta, fusedPush);
                    else
                        MoveHead(delta - 2);
                    return;
                }

                Label notTaken = NewLabel();
                if (vc is { Kind: VKind.Bool })
                {
                    il.Emit(OpCodes.Ldloc, vc.Flag!);
                    il.Emit(vc.Negated ? OpCodes.Brtrue : OpCodes.Brfalse, notTaken);
                }
                else if (vc is not null)
                {
                    // A word in a local, or in the slot an alias names, which nothing above has written.
                    LoadVec(condition, vc);
                    il.Emit(OpCodes.Call, VecZero);
                    il.Emit(OpCodes.Call, VecEqualsAll);
                    il.Emit(OpCodes.Brtrue, notTaken);
                }
                else
                {
                    SlotIsZero(condition);
                    il.Emit(OpCodes.Brtrue, notTaken);
                }

                EmitTaken(jumpIf, vd, destination, delta, fusedPush);
                il.MarkLabel(notTaken);
                MoveHead(delta - 2);
            }

            /// <summary>The taken side of a JUMPI: to a constant JUMPDEST directly, otherwise through the jump table.</summary>
            private void EmitTaken(IlInstruction jumpIf, VVal? vd, int destination, int delta, bool fusedPush)
            {
                // Whether the dispatch loop counted the landed JUMPDEST before its check (Poll).
                int landed = fusedPush ? 1 : 0;
                if (vd is not null)
                {
                    if (TryTarget(vd, out IlUnit? target))
                    {
                        MoveHead(delta - 2);
                        Poll(landedJumpDest: landed, target);
                        GoTo(target);
                    }
                    else
                    {
                        ExitWith(EvmExceptionType.InvalidJumpDestination, jumpIf.Pc + 1, delta - 2, 0);
                    }

                    return;
                }

                Label invalid = NewLabel();
                Label foreign = NewLabel();
                LoadTarget(destination, invalid, foreign);
                MoveHead(delta - 2);
                Poll(landedJumpDest: landed, target: null);
                il.Emit(OpCodes.Br, _dynamicJump);
                AddOutOfLine(() =>
                {
                    il.MarkLabel(invalid);
                    ExitWith(EvmExceptionType.InvalidJumpDestination, jumpIf.Pc + 1, delta - 2, 0);

                    il.MarkLabel(foreign);
                    MoveHead(delta - 2);
                    Poll(landedJumpDest: landed, target: null);
                    ExitContinueAtLimb();
                });
            }

            /// <summary>Loads the destination's jump case into the target local, else branches to <paramref name="invalid"/>, or to <paramref name="foreign"/> (destination in the limb local) when another chunk compiles it.</summary>
            private void LoadTarget(int slot, Label invalid, Label foreign)
            {
                // Anything above the low limb, or a low limb past the code, is no destination.
                SlotLimb(slot, 1);
                SlotLimb(slot, 2);
                il.Emit(OpCodes.Or);
                SlotLimb(slot, 3);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Brtrue, invalid);
                SlotLimb(slot, 0);
                il.Emit(OpCodes.Stloc, _limb);
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Ldc_I8, (long)codeLength);
                il.Emit(OpCodes.Bge_Un, invalid);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, JumpOrdinalField);
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ldelem_I4);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Blt, invalid);
                // The destination's case in this chunk; a JUMPDEST it does not compile, in its table's range or not, is continued at in its home.
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, JumpBaseField);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, JumpCasesField);
                il.Emit(OpCodes.Ldlen);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Bge_Un, foreign);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, JumpCasesField);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldelem_I2);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Blt, foreign);
            }

            private void ExitContinueAtLimb()
            {
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Br, _sharedContinue);
                _usesSharedContinue = true;
            }

            private void EmitDynamicJump()
            {
                il.MarkLabel(_dynamicJump);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Switch, _jumpTargets.ToArray());
                // Unreachable: the jump index only names JUMPDEST units.
                il.Emit(OpCodes.Ldc_I4, (int)EvmExceptionType.InvalidJumpDestination);
                il.Emit(OpCodes.Ret);
            }

            private void EmitExit()
            {
                il.MarkLabel(_exit);
                if (IlEvmDiagnostics.Markers)
                    il.StoreMarker(0);
                il.Emit(OpCodes.Ldarg_S, ArgVm);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateCounted);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Call, AddOpCodes);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Stfld, StateCounted);
                il.Emit(OpCodes.Ldloc, _result);
                il.Emit(OpCodes.Ret);
            }

            /// <summary>Leaves the chunk to go on at <paramref name="pc"/>, a unit of another chunk; the head is already moved.</summary>
            private void ExitContinue(int pc)
            {
                il.Emit(OpCodes.Ldc_I4, pc);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Br, _sharedContinue);
                _usesSharedContinue = true;
            }

            private void GoTo(IlUnit target)
            {
                if (_members.Contains(target))
                    il.Emit(OpCodes.Br, LabelOf(target));
                else
                    ExitContinue(target.StartPc);
            }

            /// <summary>Hands the frame back to the interpreter at <paramref name="pc"/>.</summary>
            /// <param name="refund">Gas the segment charged for the instruction at <paramref name="pc"/>, which the interpreter charges again.</param>
            /// <param name="uncount">Opcodes counted for the instruction, which the interpreter counts again.</param>
            private void Handback(int pc, int delta, ulong refund, int uncount)
            {
                if (uncount != 0)
                    AddOps(-uncount);
                ExitWith(IlDeopt, pc, delta, refund);
            }

            private void ExitWith(EvmExceptionType result, int pc, int delta, ulong gasBack)
            {
                SyncHead(delta);
                SyncGas(gasBack);
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldc_I4, pc);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stind_I);
                il.Emit(OpCodes.Ldc_I4, (int)result);
                il.Emit(OpCodes.Stloc, _result);
                il.Emit(OpCodes.Br, _exit);
            }

            private void StoreProgramCounterFromLocal()
            {
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldloc, _pc);
                il.Emit(OpCodes.Stind_I);
            }

            private void CallBody(IlInstruction instruction, IlOp op)
            {
                // Bodies see the counter past their opcode byte, as the dispatch loop passes it.
                il.Emit(OpCodes.Ldc_I4, instruction.Pc + 1);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stloc, _pc);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                if (op.Kind != IlKind.CheckedBody || op.UsesVm)
                    il.Emit(OpCodes.Ldarg_S, ArgVm);
                else
                    il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldloca, _pc);
                if (op.Kind is IlKind.CheckedBody or IlKind.Native && op.Execute is not null && IlEvmSettings.InlineBodies && inlineBudget.Remaining > 0)
                {
                    inlineBudget.Remaining--;
                    il.Emit(OpCodes.Call, op.Execute);
                }
                else
                {
                    // Through the entry point, never inlined: a CALL or RETURN body's locals would be saved and zeroed on every compiled frame entry.
                    il.Emit(OpCodes.Ldc_I8, (long)op.ExecutePointer);
                    il.Emit(OpCodes.Conv_I);
                    il.EmitCalli(typeof(EvmExceptionType), BodySignature);
                }
            }

            /// <summary>Polls for cancellation at a taken jump once the opcode count reaches the poll mark, as the dispatch loop does.</summary>
            /// <param name="landedJumpDest">1 where the dispatch loop counted the landed JUMPDEST before its check (a JUMP, or a JUMPI run with its PUSH2), else 0.</param>
            /// <remarks>
            /// A JUMPI run alone leaves the landed JUMPDEST out of the loop's poll count, though the landed unit counts it in <see cref="IlRunState.OpCodes"/>,
            /// so it moves the poll mark on by one. The loop charges the landed JUMPDEST before its check, so a jump without the gas for it
            /// faults there unpolled: compiled code skips the poll, and the landed unit's precondition faults. A jump onto the last byte ends unpolled too.
            /// </remarks>
            /// <param name="target">The unit a constant destination lands on, or null for one computed at run time, which <see cref="LoadTarget"/> left in the limb local.</param>
            private void Poll(int landedJumpDest, IlUnit? target)
            {
                if (target is not null && target.StartPc + 1 >= codeLength)
                    return;
                Label slow = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                if (landedJumpDest != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, landedJumpDest);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StatePollAt);
                il.Emit(OpCodes.Bge, slow);
                il.MarkLabel(back);
                if (landedJumpDest == 0)
                {
                    il.Emit(OpCodes.Ldarg_S, ArgState);
                    il.Emit(OpCodes.Ldarg_S, ArgState);
                    il.Emit(OpCodes.Ldfld, StatePollAt);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Stfld, StatePollAt);
                }

                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    // No gas for the landed JUMPDEST: the dispatch loop faults on it before its check.
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Ldc_I8, (long)GasCostOf.JumpDest);
                    il.Emit(OpCodes.Blt_Un, back);
                    if (target is null)
                    {
                        // A computed destination on the code's last byte: the frame ends there, unpolled.
                        il.Emit(OpCodes.Ldloc, _limb);
                        il.Emit(OpCodes.Ldc_I8, (long)(codeLength - 1));
                        il.Emit(OpCodes.Beq, back);
                    }

                    il.Emit(OpCodes.Ldarg_S, ArgState);
                    il.Emit(OpCodes.Ldarg_S, ArgState);
                    il.Emit(OpCodes.Ldfld, StateOpCodes);
                    il.Emit(OpCodes.Ldc_I4, landedJumpDest + IlPollInterval);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Stfld, StatePollAt);
                    il.Emit(OpCodes.Ldarg_S, ArgVm);
                    il.Emit(OpCodes.Call, PollCancellation);
                    il.Emit(OpCodes.Br, back);
                });
            }

            private void AddOps(int count)
            {
                if (count == 0) return;
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Ldc_I4, count);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stfld, StateOpCodes);
            }

            private void MoveHead(int delta)
            {
                if (delta == 0) return;
                il.Emit(OpCodes.Ldloc, _head);
                il.Emit(OpCodes.Ldc_I4, delta);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, _head);
            }

            private void SyncHead(int delta)
            {
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldloc, _head);
                if (delta != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, delta);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Stfld, StackHead);
            }

            private void SyncGas(ulong gasBack)
            {
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldloc, _gas);
                if (gasBack != 0)
                {
                    il.Emit(OpCodes.Ldc_I8, (long)gasBack);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Stfld, GasValue);
            }

            /// <summary>Pushes the address of the slot <paramref name="slot"/> words above the segment's starting head.</summary>
            private void Slot(int slot)
            {
                il.Emit(OpCodes.Ldloc, _frame);
                if (slot != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, slot * EvmStack.WordSize);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Add);
                }
            }

            private void SlotLimb(int slot, int limb)
            {
                il.Emit(OpCodes.Ldloc, _frame);
                int offset = slot * EvmStack.WordSize + limb * sizeof(ulong);
                if (offset != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, offset);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Ldind_I8);
            }

            private void SlotIsZero(int slot)
            {
                if (IlEvmSettings.VectorForms)
                {
                    Slot(slot);
                    il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                    il.Emit(OpCodes.Call, VecZero);
                    il.Emit(OpCodes.Call, VecEqualsAll);
                    return;
                }

                SlotLimb(slot, 0);
                SlotLimb(slot, 1);
                il.Emit(OpCodes.Or);
                SlotLimb(slot, 2);
                il.Emit(OpCodes.Or);
                SlotLimb(slot, 3);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Ldc_I8, 0L);
                il.Emit(OpCodes.Ceq);
            }

            private void StoreLimb(int slot, int limb, ulong value)
            {
                il.Emit(OpCodes.Ldloc, _frame);
                il.Emit(OpCodes.Ldc_I4, slot * EvmStack.WordSize + limb * sizeof(ulong));
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I8, (long)value);
                il.Emit(OpCodes.Stind_I8);
            }

            /// <summary>Writes a constant word to a slot in one 32-byte store: less code than four immediates, and forwardable to the next full-width load.</summary>
            private void StoreConstant(int slot, ulong u0, ulong u1, ulong u2, ulong u3)
            {
                if (!Vector256.IsHardwareAccelerated)
                {
                    StoreLimb(slot, 0, u0);
                    StoreLimb(slot, 1, u1);
                    StoreLimb(slot, 2, u2);
                    StoreLimb(slot, 3, u3);
                    return;
                }

                Slot(slot);
                if ((u0 | u1 | u2 | u3) == 0)
                {
                    il.Emit(OpCodes.Initobj, typeof(EvmWord));
                    return;
                }

                il.Emit(OpCodes.Ldc_I8, (long)u0);
                if ((u1 | u2 | u3) == 0)
                {
                    il.Emit(OpCodes.Call, VectorCreateScalar);
                }
                else
                {
                    il.Emit(OpCodes.Ldc_I8, (long)u1);
                    il.Emit(OpCodes.Ldc_I8, (long)u2);
                    il.Emit(OpCodes.Ldc_I8, (long)u3);
                    il.Emit(OpCodes.Call, VectorCreate);
                }
                il.Emit(OpCodes.Call, VectorAsByte);
                il.Emit(OpCodes.Stobj, typeof(EvmWord));
            }

            /// <summary>Writes the 64-bit value on the evaluation stack to a slot as a word, its upper limbs zero, in one store.</summary>
            private void StoreSmallWord(int slot, Action value)
            {
                if (!Vector256.IsHardwareAccelerated)
                {
                    Slot(slot);
                    value();
                    il.Emit(OpCodes.Stind_I8);
                    StoreLimb(slot, 1, 0);
                    StoreLimb(slot, 2, 0);
                    StoreLimb(slot, 3, 0);
                    return;
                }

                Slot(slot);
                value();
                il.Emit(OpCodes.Call, VectorCreateScalar);
                il.Emit(OpCodes.Call, VectorAsByte);
                il.Emit(OpCodes.Stobj, typeof(EvmWord));
            }

            private void Swap(int a, int b)
            {
                Slot(a);
                il.Emit(OpCodes.Ldobj, typeof(EvmWord));
                il.Emit(OpCodes.Stloc, _word);
                Slot(a);
                Slot(b);
                il.Emit(OpCodes.Ldobj, typeof(EvmWord));
                il.Emit(OpCodes.Stobj, typeof(EvmWord));
                Slot(b);
                il.Emit(OpCodes.Ldloc, _word);
                il.Emit(OpCodes.Stobj, typeof(EvmWord));
            }
        }
    }
}
