// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis.IL.CompilerModes.PartialAOT;
using Nethermind.Int256;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Threading.Tasks;

using static Nethermind.Evm.CodeAnalysis.IL.WordEmit;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using static Nethermind.Evm.CodeAnalysis.IL.StackEmit;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes.FullAOT;

internal class FullAotOpcodeEmitter<T> : PartialAotOpcodeEmitter<T>
{
    public FullAotOpcodeEmitter()
    {
        // statefull opcode
        Instruction[] statefullOpcodes = [
            Instruction.CALL,
            Instruction.CALLCODE,
            Instruction.DELEGATECALL,
            Instruction.STATICCALL,
            Instruction.CREATE,
            Instruction.CREATE2,
            ];

        foreach (Instruction instruction in statefullOpcodes)
        {
            switch (instruction)
            {
                case Instruction.CALL:
                case Instruction.CALLCODE:
                case Instruction.DELEGATECALL:
                case Instruction.STATICCALL:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, currentSubSegment, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels) =>
                    {
                        MethodInfo callMethodTracign = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.InstructionCall), BindingFlags.Static | BindingFlags.Public)
                            .MakeGenericMethod(typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing));

                        MethodInfo callMethodNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.InstructionCall), BindingFlags.Static | BindingFlags.Public)
                            .MakeGenericMethod(typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing));

                        using Local toPushToStack = method.DeclareLocal(typeof(UInt256?));
                        using Local newStateToExe = method.DeclareLocal<Object>();
                        Label happyPath = method.DefineLabel();

                        envLoader.LoadVmState(method, locals, false);
                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        envLoader.LoadLogger(method, locals, false);

                        method.LoadConstant((int)instruction);

                        int index = 1;
                        // load gasLimit
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load codeSource
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetAddress);

                        // load callvalue
                        if (instruction is Instruction.CALL)
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                            method.Call(Word.GetUInt256);
                        }
                        else
                        {
                            method.LoadField(typeof(UInt256).GetField(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
                        }

                        // load dataoffset
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load datalength
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load outputOffset
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load outputLength
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index);
                        method.Call(Word.GetUInt256);

                        method.LoadLocalAddress(toPushToStack);

                        envLoader.LoadReturnDataBuffer(method, locals, true);

                        method.LoadLocalAddress(newStateToExe);

                        if (ilCompilerConfig.BakeInTracingInAotModes)
                        {
                            method.Call(callMethodTracign);
                        }
                        else
                        {
                            method.Call(callMethodNotTracing);
                        }
                        method.StoreLocal(locals.uint32A);

                        if (ilCompilerConfig.BakeInTracingInAotModes)
                        {
                            UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, opcodeMetadata);
                            EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                        }
                        else
                        {
                            UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, i, currentSubSegment);
                        }

                        method.LoadLocal(locals.uint32A);
                        method.LoadConstant((int)EvmExceptionType.None);
                        method.BranchIfEqual(happyPath);

                        envLoader.LoadResult(method, locals, true);
                        method.Duplicate();
                        method.LoadLocal(locals.uint32A);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                        method.LoadConstant((int)ContractState.Failed);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

                        envLoader.LoadGasAvailable(method, locals, true);
                        method.LoadLocal(locals.gasAvailable);
                        method.StoreIndirect<long>();
                        method.FakeBranch(escapeLabels.exitLabel); ;

                        method.MarkLabel(happyPath);

                        Label skipStateMachineScheduling = method.DefineLabel();

                        method.LoadLocal(newStateToExe);
                        method.LoadNull();
                        method.BranchIfEqual(skipStateMachineScheduling);

                        method.LoadLocal(newStateToExe);
                        method.Call(GetPropertyInfo(typeof(VirtualMachine.CallResult), nameof(VirtualMachine.CallResult.BoxedEmpty), false, out _));
                        method.Call(typeof(Object).GetMethod(nameof(Object.ReferenceEquals), BindingFlags.Static | BindingFlags.Public));
                        method.BranchIfTrue(skipStateMachineScheduling);

                        // cast object to CallResult and store it in 
                        envLoader.LoadResult(method, locals, true);
                        method.Duplicate();
                        method.LoadLocal(newStateToExe);
                        method.CastClass(typeof(EvmState));
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));

                        method.LoadConstant((int)ContractState.Halted);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                        method.FakeBranch(escapeLabels.returnLabel);

                        method.MarkLabel(skipStateMachineScheduling);
                        Label hasNoItemsToPush = method.DefineLabel();

                        method.LoadLocalAddress(toPushToStack);
                        method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.HasValue)).GetGetMethod());
                        method.BranchIfTrue(hasNoItemsToPush);

                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index);
                        method.LoadLocalAddress(toPushToStack);
                        method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.Value)).GetGetMethod());
                        method.Call(Word.SetUInt256);

                        method.MarkLabel(hasNoItemsToPush);
                    });
                    break;
                case Instruction.CREATE:
                case Instruction.CREATE2:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, currentSubSegment, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels) =>
                    {
                        MethodInfo callMethodTracign = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.InstructionCreate), BindingFlags.Static | BindingFlags.Public)
                            .MakeGenericMethod(typeof(VirtualMachine.IsTracing));

                        MethodInfo callMethodNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.InstructionCreate), BindingFlags.Static | BindingFlags.Public)
                            .MakeGenericMethod(typeof(VirtualMachine.NotTracing));

                        using Local toPushToStack = method.DeclareLocal(typeof(UInt256?));
                        using Local newStateToExe = method.DeclareLocal<Object>();
                        Label happyPath = method.DefineLabel();

                        envLoader.LoadVmState(method, locals, false);

                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        envLoader.LoadLogger(method, locals, false);

                        method.LoadConstant((int)instruction);

                        int index = 1;

                        // load value
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load memory offset
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load initcode len
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load callvalue
                        if (instruction is Instruction.CREATE2)
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                            method.Call(Word.GetMutableSpan);
                        }
                        else
                        {
                            // load empty span
                            method.Call(typeof(Span<byte>).GetProperty(nameof(Span<byte>.Empty), BindingFlags.Static | BindingFlags.Public).GetGetMethod());
                        }

                        method.LoadLocalAddress(toPushToStack);

                        envLoader.LoadReturnDataBuffer(method, locals, true);

                        method.LoadLocalAddress(newStateToExe);

                        if (ilCompilerConfig.BakeInTracingInAotModes)
                        {
                            method.Call(callMethodTracign);
                        }
                        else
                        {
                            method.Call(callMethodNotTracing);
                        }
                        method.StoreLocal(locals.uint32A);


                        if (ilCompilerConfig.BakeInTracingInAotModes)
                        {
                            UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, opcodeMetadata);
                            EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                        }
                        else
                        {
                            UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, i, currentSubSegment);
                        }

                        method.LoadLocal(locals.uint32A);
                        method.LoadConstant((int)EvmExceptionType.None);
                        method.BranchIfEqual(happyPath);

                        envLoader.LoadResult(method, locals, true);
                        method.Duplicate();
                        method.LoadLocal(locals.uint32A);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                        method.LoadConstant((int)ContractState.Failed);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

                        envLoader.LoadGasAvailable(method, locals, true);
                        method.LoadLocal(locals.gasAvailable);
                        method.StoreIndirect<long>();
                        method.FakeBranch(escapeLabels.exitLabel);

                        method.MarkLabel(happyPath);

                        Label skipStateMachineScheduling = method.DefineLabel();

                        method.LoadLocal(newStateToExe);
                        method.LoadNull();
                        method.BranchIfEqual(skipStateMachineScheduling);

                        method.LoadLocal(newStateToExe);
                        method.Call(GetPropertyInfo(typeof(VirtualMachine.CallResult), nameof(VirtualMachine.CallResult.BoxedEmpty), false, out _));
                        method.Call(typeof(Object).GetMethod(nameof(Object.ReferenceEquals), BindingFlags.Static | BindingFlags.Public));
                        method.BranchIfTrue(skipStateMachineScheduling);

                        // cast object to CallResult and store it in 
                        envLoader.LoadResult(method, locals, true);
                        method.Duplicate();
                        method.LoadLocal(newStateToExe);
                        method.CastClass(typeof(EvmState));
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));

                        method.LoadConstant((int)ContractState.Halted);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                        method.Branch(escapeLabels.returnLabel);

                        method.MarkLabel(skipStateMachineScheduling);
                        Label hasNoItemsToPush = method.DefineLabel();

                        method.LoadLocalAddress(toPushToStack);
                        method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.HasValue)).GetGetMethod());
                        method.BranchIfTrue(hasNoItemsToPush);

                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index);
                        method.LoadLocalAddress(toPushToStack);
                        method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.Value)).GetGetMethod());
                        method.Call(Word.SetUInt256);

                        method.MarkLabel(hasNoItemsToPush);
                    });
                    break;
            }
        }
    }
}

