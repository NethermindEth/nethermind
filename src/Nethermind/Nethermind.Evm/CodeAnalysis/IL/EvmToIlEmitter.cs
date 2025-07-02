// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Config;
using Nethermind.Int256;
using Nethermind.State;
using Sigil;
using static Nethermind.Evm.CodeAnalysis.IL.WordEmit;
using static Nethermind.Evm.CodeAnalysis.IL.UnsafeEmit;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using static Nethermind.Evm.CodeAnalysis.IL.OpcodeEmitters;

namespace Nethermind.Evm.CodeAnalysis.IL;

internal static class OpcodeEmitter
{
    public static void GetOpcodeILEmitter<TDelegateType>(
        this Emit<TDelegateType> method,
        ICodeInfo codeinfo, Instruction op,
        IVMConfig ilCompilerConfig,
        ContractCompilerMetadata contractMetadata,
        SubSegmentMetadata currentSubSegment,
        int pc, OpcodeMetadata opcodeMetadata,
        Locals<TDelegateType> locals,
        EnvirementLoader envirementLoader,
        Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        switch (op)
        {
            case Instruction.JUMPDEST:
                return;

            case Instruction.JUMP:
                EmitJumpInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.JUMPI:
                EmitJumpiInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;

            case Instruction.POP:
                return;
            case Instruction.STOP:
                EmitStopInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CHAINID:
                EmitChainIdInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.NOT:
                EmitNotInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.PUSH0:
            case Instruction.PUSH1:
            case Instruction.PUSH2:
            case Instruction.PUSH3:
            case Instruction.PUSH4:
            case Instruction.PUSH5:
            case Instruction.PUSH6:
            case Instruction.PUSH7:
            case Instruction.PUSH8:
                EmiPush_sInstructions(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.PUSH9:
            case Instruction.PUSH10:
            case Instruction.PUSH11:
            case Instruction.PUSH12:
            case Instruction.PUSH13:
            case Instruction.PUSH14:
            case Instruction.PUSH15:
            case Instruction.PUSH16:
            case Instruction.PUSH17:
            case Instruction.PUSH18:
            case Instruction.PUSH19:
            case Instruction.PUSH20:
            case Instruction.PUSH21:
            case Instruction.PUSH22:
            case Instruction.PUSH23:
            case Instruction.PUSH24:
            case Instruction.PUSH25:
            case Instruction.PUSH26:
            case Instruction.PUSH27:
            case Instruction.PUSH28:
            case Instruction.PUSH29:
            case Instruction.PUSH30:
            case Instruction.PUSH31:
            case Instruction.PUSH32:
                EmitPush_bInstructions(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.ADD:
                EmitBinaryUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                return;
            case Instruction.SUB:
                EmitSubInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.MUL:
                EmitMulInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;

            case Instruction.MOD:
                EmitModInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SMOD:
                EmitSModInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.DIV:
                EmitDivInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SDIV:
                EmitSDivInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.ADDMOD:
                EmitAddModeInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.MULMOD:
                EmitMulModInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SHL:
                EmitShiftUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), isLeft: true, evmExceptionLabels);
                return;
            case Instruction.SHR:
                EmitShiftUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), isLeft: false, evmExceptionLabels);
                return;
            case Instruction.SAR:
                EmitShiftInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), evmExceptionLabels);
                return;
            case Instruction.AND:
                EmitBitwiseUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseAnd), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                return;
            case Instruction.OR:
                EmitBitwiseUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseOr), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                return;
            case Instruction.XOR:
                EmitBitwiseUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.Xor), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                return;
            case Instruction.EXP:
                EmitExpInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.LT:
                EmitComparisonUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels);
                return;
            case Instruction.GT:
                EmitComparisonUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels);
                return;
            case Instruction.SLT:
                EmitComparisonInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), false, evmExceptionLabels);
                return;
            case Instruction.SGT:
                EmitComparisonInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), true, evmExceptionLabels);
                return;
            case Instruction.EQ:
                EmitEqInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.ISZERO:
                EmitIsZeroInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.DUP1:
            case Instruction.DUP2:
            case Instruction.DUP3:
            case Instruction.DUP4:
            case Instruction.DUP5:
            case Instruction.DUP6:
            case Instruction.DUP7:
            case Instruction.DUP8:
            case Instruction.DUP9:
            case Instruction.DUP10:
            case Instruction.DUP11:
            case Instruction.DUP12:
            case Instruction.DUP13:
            case Instruction.DUP14:
            case Instruction.DUP15:
            case Instruction.DUP16:
                EmitDupInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SWAP1:
            case Instruction.SWAP2:
            case Instruction.SWAP3:
            case Instruction.SWAP4:
            case Instruction.SWAP5:
            case Instruction.SWAP6:
            case Instruction.SWAP7:
            case Instruction.SWAP8:
            case Instruction.SWAP9:
            case Instruction.SWAP10:
            case Instruction.SWAP11:
            case Instruction.SWAP12:
            case Instruction.SWAP13:
            case Instruction.SWAP14:
            case Instruction.SWAP15:
            case Instruction.SWAP16:
                EmitSwapInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CODESIZE:
                EmitCodeSizeInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.PC:
                EmitPcInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.COINBASE:
                EmitCoinbaseInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.TIMESTAMP:
                EmitTimestampInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.NUMBER:
                EmitNumberInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.GASLIMIT:
                EmitGasLimitInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CALLER:
                EmitCallerInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.ADDRESS:
                EmitAddressInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.ORIGIN:
                EmitOriginInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CALLVALUE:
                EmitCallValueInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.GASPRICE:
                EmitGasPriceInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CALLDATACOPY:
                EmitCallDataCopyInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CALLDATALOAD:
                EmitCalldataLoadInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CALLDATASIZE:
                EmitCalldataSizeInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.MSIZE:
                EmitMSizeInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.MSTORE:
                EmitMStoreInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.MSTORE8:
                EmitMStore8Instruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.MLOAD:
                EmitMLoadInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.MCOPY:
                EmitMCopyInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.KECCAK256:
                EmitKeccak256Instruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.BYTE:
                EmitByteInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CODECOPY:
                EmitCodeCopyInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.GAS:
                EmitGasInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.RETURNDATASIZE:
                EmitReturnDataSizeInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.RETURNDATACOPY:
                EmitReturnDataCopyInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.RETURN or Instruction.REVERT:
                EmitReturnOrRevertInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.BASEFEE:
                EmitBaseFeeInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.BLOBBASEFEE:
                EmitBlobBaseFeeInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.PREVRANDAO:
                EmitPrevRandaoInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.BLOBHASH:
                EmitBlobHashInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.BLOCKHASH:
                EmitBlockHashInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SIGNEXTEND:
                EmitSignExtendInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.LOG0:
            case Instruction.LOG1:
            case Instruction.LOG2:
            case Instruction.LOG3:
            case Instruction.LOG4:
                EmitLogInstructions(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.TSTORE:
                EmitTStoreInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.TLOAD:
                EmitTLoadInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SSTORE:
                EmitSStoreInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SLOAD:
                EmitSLoadInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.EXTCODESIZE:
                EmitExtcodeSizeInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.EXTCODECOPY:
                EmitExtcodeCopyInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.EXTCODEHASH:
                EmitExtcodeHashInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SELFBALANCE:
                EmitSelfBalanceInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.BALANCE:
                EmitBalanceInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.SELFDESTRUCT:
                EmitSelfDestructInstruction(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CALL:
            case Instruction.CALLCODE:
            case Instruction.DELEGATECALL:
            case Instruction.STATICCALL:
                EmitCallInstructions(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            case Instruction.CREATE:
            case Instruction.CREATE2:
                EmitCreateInstructions(method, codeinfo, op, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcodeMetadata, envirementLoader, locals, evmExceptionLabels, escapeLabels);
                return;
            default:
                method.FakeBranch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                return;
        }
    }

}
internal static class OpcodeEmitters
{
    internal static void EmitChainIdInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadChainId(method, locals);
        method.Call(Word.SetKeccakByRef);
    }
    internal static void EmitLogInstructions<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        var topicsCount = (sbyte)(op - Instruction.LOG0);
        using Local logEntry = method.DeclareLocal<LogEntry>(locals.GetLocalName());
        using Local keccak = method.DeclareLocal(typeof(ValueHash256), locals.GetLocalName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A); // position
        method.Call(Word.GetUInt256ByRef);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256B); // length
        method.Call(Word.GetUInt256ByRef);

        // UpdateMemoryCost
        envLoader.LoadVmState(method, locals, false);


        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A); // position
        method.LoadLocalAddress(locals.uint256B); // length
        method.Call(
            typeof(VirtualMachineDependencies).GetMethod(
                nameof(VirtualMachineDependencies.UpdateMemoryCost)
            )
        );
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        // update locals.gasAvailable
        method.LoadLocal(locals.gasAvailable);
        method.LoadConstant(topicsCount * GasCostOf.LogTopic);
        method.Convert<ulong>();
        method.LoadLocalAddress(locals.uint256B); // length
        method.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
        method.Convert<ulong>();
        method.LoadConstant(GasCostOf.LogData);
        method.Multiply();
        method.Add();
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable); // locals.gasAvailable -= gasCost
        method.LoadConstant((ulong)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadEnvByRef(method, locals);
        method.LoadField(
            GetFieldInfo(
                typeof(ExecutionEnvironment),
                nameof(ExecutionEnvironment.ExecutingAccount)
            )
        );

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A); // position
        method.LoadLocalAddress(locals.uint256B); // length
        method.Call(
            typeof(EvmPooledMemory).GetMethod(
                nameof(EvmPooledMemory.Load),
                [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]
            )
        );
        method.StoreLocal(locals.localReadOnlyMemory);
        method.LoadLocalAddress(locals.localReadOnlyMemory);
        method.Call(typeof(ReadOnlyMemory<byte>).GetMethod(nameof(ReadOnlyMemory<byte>.ToArray)));

        method.LoadConstant(topicsCount);
        method.NewArray<Hash256>();
        for (var k = 0; k < topicsCount; k++)
        {
            method.Duplicate();
            method.LoadConstant(k);
            method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0) - 2, k + 1);
            method.Call(Word.GetKeccak);
            method.StoreLocal(keccak);
            method.LoadLocalAddress(keccak);
            method.NewObject(typeof(Hash256), typeof(ValueHash256).MakeByRefType());
            method.StoreElement<Hash256>();
        }
        // Creat an LogEntry Object from Items on the Stack
        method.NewObject(typeof(LogEntry), typeof(Address), typeof(byte[]), typeof(Hash256[]));
        method.StoreLocal(logEntry);

        envLoader.LoadVmState(method, locals, false);

        using Local accessTrackerLocal = method.DeclareLocal<StackAccessTracker>(locals.GetLocalName());
        method.Call(typeof(EvmState).GetProperty(nameof(EvmState.AccessTracker), BindingFlags.Instance | BindingFlags.Public).GetGetMethod());
        method.LoadObject<StackAccessTracker>();
        method.StoreLocal(accessTrackerLocal);

        method.LoadLocalAddress(accessTrackerLocal);
        method.CallVirtual(GetPropertyInfo(typeof(StackAccessTracker), nameof(StackAccessTracker.Logs), getSetter: false, out _));
        method.LoadLocal(logEntry);
        method.CallVirtual(
            typeof(ICollection<LogEntry>).GetMethod(nameof(ICollection<LogEntry>.Add))
        );
    }

    internal static void EmitSignExtendInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label signIsNegative = method.DefineLabel(locals.GetLabelName());
        Label endOfOpcodeHandling = method.DefineLabel(locals.GetLabelName());
        Label argumentGt32 = method.DefineLabel(locals.GetLabelName());
        using Local wordSpan = method.DeclareLocal(typeof(Span<byte>), locals.GetLocalName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Duplicate();
        method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
        method.StoreLocal(locals.uint32A);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocalAddress(locals.uint256A);
        method.LoadConstant(32);
        method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        method.BranchIfFalse(argumentGt32);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Call(Word.GetMutableSpan);
        method.StoreLocal(wordSpan);

        method.LoadConstant((uint)31);
        method.LoadLocal(locals.uint32A);
        method.Subtract();
        method.StoreLocal(locals.uint32A);

        method.LoadItemFromSpan<TDelegateType, byte>(locals.uint32A, false, wordSpan);
        method.LoadIndirect<byte>();
        method.Convert<sbyte>();
        method.LoadConstant(0);
        method.BranchIfLess(signIsNegative);

        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesZero32), BindingFlags.Static | BindingFlags.Public));
        method.Branch(endOfOpcodeHandling);

        method.MarkLabel(signIsNegative);
        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesMax32), BindingFlags.Static | BindingFlags.Public));

        method.MarkLabel(endOfOpcodeHandling);
        method.LoadConstant(0);
        method.LoadLocal(locals.uint32A);
        method.EmitAsSpan();
        method.StoreLocal(locals.localSpan);

        method.LoadLocalAddress(locals.localSpan);
        method.LoadLocalAddress(wordSpan);
        method.LoadConstant(0);
        method.LoadLocal(locals.uint32A);
        method.Call(typeof(Span<byte>).GetMethod(nameof(Span<byte>.Slice), [typeof(int), typeof(int)]));
        method.Call(typeof(Span<byte>).GetMethod(nameof(Span<byte>.CopyTo), [typeof(Span<byte>)]));

        method.MarkLabel(argumentGt32);
    }

    internal static void EmitBlockHashInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label blockHashIsNull = method.DefineLabel(locals.GetLabelName());
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocalAddress(locals.uint256A);
        method.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
        method.Duplicate();
        method.StoreLocal(locals.int64A);

        envLoader.LoadHeader(method, locals);
        method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.Number), false, out _));
        method.BranchIfGreaterOrEqual(blockHashIsNull); // blockhash is assumed null if number >= current block number

        envLoader.LoadBlockhashProvider(method, locals);
        envLoader.LoadHeader(method, locals);
        method.LoadLocal(locals.int64A);
        envLoader.LoadSpec(method, locals);
        method.CallVirtual(typeof(IBlockhashProvider).GetMethod(nameof(IBlockhashProvider.GetBlockhash), [typeof(BlockHeader), typeof(long), typeof(IReleaseSpec)]));
        method.Duplicate();
        method.StoreLocal(locals.hash256);
        method.LoadNull();
        method.BranchIfEqual(blockHashIsNull);

        // blockHash is not null
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocal(locals.hash256);
        method.Call(GetPropertyInfo(typeof(Hash256), nameof(Hash256.Bytes), false, out _));
        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
        method.Call(Word.SetReadOnlySpan);
        method.Branch(endOfOpcode);

        // is null
        method.MarkLabel(blockHashIsNull);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

        method.MarkLabel(endOfOpcode);
    }

    internal static void EmitBlobHashInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label blobVersionedHashNotFound = method.DefineLabel(locals.GetLabelName());
        Label indexTooLarge = method.DefineLabel(locals.GetLabelName());
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());

        using Local byteMatrix = method.DeclareLocal(typeof(byte[][]), locals.GetLocalName());
        envLoader.LoadTxContext(method, locals, true);
        method.LoadFieldAddress(GetFieldInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes)));
        method.StoreLocal(byteMatrix);

        method.LoadLocal(byteMatrix);
        method.LoadNull();
        method.BranchIfEqual(blobVersionedHashNotFound);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocal(byteMatrix);
        method.Call(GetPropertyInfo(typeof(byte[][]), nameof(Array.Length), false, out _));
        method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        method.BranchIfFalse(indexTooLarge);

        method.LoadLocal(byteMatrix);
        method.LoadLocal(locals.uint256A);
        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
        method.Convert<int>();
        method.LoadElement<byte[]>();
        method.StoreLocal(locals.localArray);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocal(locals.localArray);
        method.Call(Word.SetArray);
        method.Branch(endOfOpcode);

        method.MarkLabel(blobVersionedHashNotFound);
        method.MarkLabel(indexTooLarge);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.MarkLabel(endOfOpcode);
    }

    internal static void EmitPrevRandaoInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label isPostMergeBranch = method.DefineLabel(locals.GetLabelName());
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);

        envLoader.LoadBlockContext(method, locals, true);
        method.LoadField(GetFieldInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header)));

        method.Duplicate();
        method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.IsPostMerge), false, out _));
        method.BranchIfFalse(isPostMergeBranch);
        method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.Random), false, out _));
        method.Call(GetPropertyInfo(typeof(Hash256), nameof(Hash256.Bytes), false, out _));
        method.Call(Word.SetMutableSpan);
        method.Branch(endOfOpcode);

        method.MarkLabel(isPostMergeBranch);
        method.LoadFieldAddress(GetFieldInfo(typeof(BlockHeader), nameof(BlockHeader.Difficulty)));
        method.Call(Word.SetUInt256ByRef);

        method.MarkLabel(endOfOpcode);
    }

    internal static void EmitBlobBaseFeeInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        using Local ulongNullable = method.DeclareLocal(typeof(ulong?), locals.GetLocalName());

        envLoader.LoadBlockContext(method, locals, true);
        method.LoadField(GetFieldInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header)));
        method.CallVirtual(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.ExcessBlobGas), false, out _));
        method.StoreLocal(ulongNullable);

        method.LoadLocalAddress(ulongNullable);
        method.Call(typeof(ulong?).GetProperty(nameof(Nullable<ulong>.HasValue)).GetGetMethod());
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));

        method.LoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadBlockContext(method, locals, true);
        method.LoadFieldAddress(GetFieldInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.BlobBaseFee)));

        method.Call(Word.SetKeccakByRef);
    }

    internal static void EmitBaseFeeInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadHeaderFieldByRef(method, locals, nameof(BlockHeader.BaseFeePerGas));
        method.Call(Word.SetUInt256ByRef);
    }

    internal static void EmitReturnOrRevertInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadResult(method, locals, true);
        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Load), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));

        envLoader.LoadResult(method, locals, true);
        switch (op)
        {
            case Instruction.REVERT:
                method.LoadConstant((int)ContractState.Revert);
                break;
            case Instruction.RETURN:
                method.LoadConstant((int)ContractState.Return);
                break;
        }
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
        method.FakeBranch(escapeLabels.returnLabel);
    }

    internal static void EmitReturnDataCopyInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());


        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocalAddress(locals.uint256B);
        method.LoadLocalAddress(locals.uint256C);
        method.LoadLocalAddress(locals.uint256R);
        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.AddOverflow)));
        method.LoadLocalAddress(locals.uint256R);

        envLoader.LoadReturnDataBuffer(method, locals, true);
        method.Call(typeof(ReadOnlyMemory<byte>).GetProperty(nameof(ReadOnlyMemory<byte>.Length)).GetMethod!);
        method.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        method.Or();
        method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.AccessViolation));

        method.LoadLocal(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256C);
        method.LoadLocalAddress(locals.lbool);
        method.Call(typeof(EvmInstructions).GetMethod(nameof(EvmInstructions.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
        method.LoadConstant(GasCostOf.Memory);
        method.Multiply();
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        // Note : check if c + b > returnData.Size

        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
        method.BranchIfTrue(endOfOpcode);

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadReturnDataBuffer(method, locals, false);
        method.LoadLocalAddress(locals.uint256B);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(MethodInfo<UInt256>("op_Explicit", typeof(int), new[] { typeof(UInt256).MakeByRefType() }));
        method.LoadConstant((int)PadDirection.Right);
        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
        method.StoreLocal(locals.localZeroPaddedSpan);

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.localZeroPaddedSpan);
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

        method.MarkLabel(endOfOpcode);
    }

    internal static void EmitReturnDataSizeInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadReturnDataBuffer(method, locals, true);
        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
    }

    internal static void EmitGasInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        method.LoadLocal(locals.gasAvailable);
        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
    }

    internal static void EmitCodeCopyInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(Word.GetUInt256ByRef);


        method.LoadLocal(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256C);
        method.LoadLocalAddress(locals.lbool);
        method.Call(typeof(EvmInstructions).GetMethod(nameof(EvmInstructions.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
        method.LoadConstant(GasCostOf.Memory);
        method.Multiply();
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);


        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
        method.BranchIfTrue(endOfOpcode);

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadMachineCode(method, locals, true);
        method.LoadConstant(codeinfo.Code.Length);
        method.LoadLocalAddress(locals.uint256B);
        method.LoadLocalAddress(locals.uint256C);
        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
        method.Convert<int>();
        method.LoadConstant((int)PadDirection.Right);
        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(byte).MakeByRefType(), typeof(int), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
        method.StoreLocal(locals.localZeroPaddedSpan);

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.localZeroPaddedSpan);
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

        method.MarkLabel(endOfOpcode);
    }

    internal static void EmitByteInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Duplicate();
        method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
        method.StoreLocal(locals.uint32A);
        method.StoreLocal(locals.wordRef256A);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Call(Word.GetReadOnlySpan);
        method.StoreLocal(locals.localReadonOnlySpan);


        Label pushZeroLabel = method.DefineLabel(locals.GetLabelName());
        Label endOfInstructionImpl = method.DefineLabel(locals.GetLabelName());
        method.EmitCheck(nameof(Word.IsShort), locals.wordRef256A);
        method.BranchIfFalse(pushZeroLabel);
        method.LoadLocal(locals.wordRef256A);
        method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
        method.LoadConstant(Word.Size);
        method.BranchIfGreaterOrEqual(pushZeroLabel);
        method.LoadLocal(locals.wordRef256A);
        method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
        method.LoadConstant(0);
        method.BranchIfLess(pushZeroLabel);

        method.LoadLocalAddress(locals.localReadonOnlySpan);
        method.LoadLocal(locals.uint32A);
        method.Call(typeof(ReadOnlySpan<byte>).GetMethod("get_Item"));
        method.LoadIndirect<byte>();
        method.Convert<uint>();
        method.StoreLocal(locals.uint32A);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocal(locals.uint32A);
        method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
        method.Branch(endOfInstructionImpl);

        method.MarkLabel(pushZeroLabel);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.MarkLabel(endOfInstructionImpl);
    }

    internal static void EmitKeccak256Instruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        MethodInfo refWordToRefValueHashMethod = GetAsMethodInfo<Word, ValueHash256>();

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);


        method.LoadLocal(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256B);
        method.LoadLocalAddress(locals.lbool);
        method.Call(typeof(EvmInstructions).GetMethod(nameof(EvmInstructions.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
        method.LoadConstant(GasCostOf.Sha3Word);
        method.Multiply();
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Call(refWordToRefValueHashMethod);
        method.Call(typeof(KeccakCache).GetMethod(nameof(KeccakCache.ComputeTo), [typeof(ReadOnlySpan<byte>), typeof(ValueHash256).MakeByRefType()]));
    }

    internal static void EmitMCopyInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocal(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256C);
        method.LoadLocalAddress(locals.lbool);
        method.Call(typeof(EvmInstructions).GetMethod(nameof(EvmInstructions.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
        method.LoadConstant(GasCostOf.VeryLow);
        method.Multiply();
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Max)));
        method.StoreLocal(locals.uint256R);
        method.LoadLocalAddress(locals.uint256R);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256B);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
    }

    internal static void EmitMLoadInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);

        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BigInt32), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        method.StoreLocal(locals.uint256B);

        method.LoadLocalAddress(locals.uint256B);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
        method.StoreLocal(locals.localReadonOnlySpan);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocal(locals.localReadonOnlySpan);
        method.Call(Word.SetReadOnlySpan);
    }

    internal static void EmitMStore8Instruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.CallGetter(Word.GetByte0, BitConverter.IsLittleEndian);
        method.StoreLocal(locals.byte8A);

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadConstant(1);
        method.Call(ConvertionExplicit<UInt256, int>());
        method.StoreLocal(locals.uint256C);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocal(locals.byte8A);

        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
    }

    internal static void EmitMStoreInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.StoreLocal(locals.wordRef256B);

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadConstant(Word.Size);
        method.Call(ConvertionExplicit<UInt256, int>());
        method.StoreLocal(locals.uint256C);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocal(locals.wordRef256B);
        method.Call(Word.GetMutableSpan);
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
    }

    internal static void EmitMSizeInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);

        envLoader.LoadMemoryByRef(method, locals);
        method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
    }

    internal static void EmitNumberInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadBlockContext(method, locals, true);
        method.LoadField(GetFieldInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header)));

        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
    }

    internal static void EmitGasLimitInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadBlockContext(method, locals, true);
        method.LoadField(GetFieldInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.GasLimit)));
        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
    }

    internal static void EmitCallerInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadEnvByRef(method, locals);

        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
        method.Call(Word.SetAddress);
    }

    internal static void EmitAddressInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadEnvByRef(method, locals);

        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
        method.Call(Word.SetAddress);
    }

    internal static void EmitOriginInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadTxContext(method, locals, true);
        method.LoadFieldAddress(GetFieldInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin)));
        method.Call(Word.SetKeccakByRef);
    }

    internal static void EmitCallValueInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadEnvByRef(method, locals);
        method.LoadFieldAddress(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
        method.Call(Word.SetUInt256ByRef);
    }

    internal static Label EmitCallDataCopyInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocal(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256C);
        method.LoadLocalAddress(locals.lbool);
        method.Call(typeof(EvmInstructions).GetMethod(nameof(EvmInstructions.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
        method.LoadConstant(GasCostOf.Memory);
        method.Multiply();
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
        method.BranchIfTrue(endOfOpcode);

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadCalldata(method, locals, false);
        method.LoadLocalAddress(locals.uint256B);
        method.LoadLocal(locals.uint256C);
        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
        method.Convert<int>();
        method.LoadConstant((int)PadDirection.Right);
        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
        method.StoreLocal(locals.localZeroPaddedSpan);

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.localZeroPaddedSpan);
        method.CallVirtual(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

        method.MarkLabel(endOfOpcode);
        return endOfOpcode;
    }

    internal static void EmitCalldataLoadInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

        envLoader.LoadCalldata(method, locals, false);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadConstant(Word.Size);
        method.LoadConstant((int)PadDirection.Right);
        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
        method.Call(Word.SetZeroPaddedSpan);
    }

    internal static void EmitCalldataSizeInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadCalldata(method, locals, true);
        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
    }

    internal static void EmitGasPriceInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadTxContext(method, locals, true);
        method.LoadFieldAddress(GetFieldInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice)));
        method.Call(Word.SetUInt256ByRef);
    }

    internal static void EmitTimestampInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadBlockContext(method, locals, true);
        method.LoadField(GetFieldInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header)));

        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
    }

    internal static void EmitCoinbaseInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadBlockContext(method, locals, true);
        method.LoadField(GetFieldInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header)));

        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
        method.Call(Word.SetAddress);
    }

    internal static void EmitPcInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        method.LoadConstant(pc);
        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
    }

    internal static void EmitCodeSizeInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        method.LoadConstant(codeinfo.Code.Length);
        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
    }

    internal static void EmitSwapInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        int count = (int)op - (int)Instruction.SWAP1 + 1;

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), count + 1);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadObject(typeof(Word));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), count + 1);
        method.LoadObject(typeof(Word));
        method.StoreObject(typeof(Word));

        method.StoreObject(typeof(Word));
    }

    internal static void EmitDupInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        var count = (int)op - (int)Instruction.DUP1 + 1;
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), count);
        method.LoadObject(typeof(Word));
        method.StoreObject(typeof(Word));
    }

    internal static void EmitIsZeroInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Duplicate();
        method.Duplicate();
        method.EmitCheck(nameof(Word.IsZero));
        method.StoreLocal(locals.lbool);
        method.Call(Word.SetToZero);
        method.LoadLocal(locals.lbool);
        method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
    }

    internal static void EmitEqInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        var refWordToRefByteMethod = GetAsMethodInfo<Word, byte>();
        var readVector256Method = GetReadUnalignedMethodInfo<Vector256<byte>>();
        var writeVector256Method = GetWriteUnalignedMethodInfo<Vector256<byte>>();
        MethodInfo operationUnegenerified = typeof(Vector256).GetMethod(nameof(Vector256.EqualsAll), BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(byte));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Call(refWordToRefByteMethod);
        method.Call(readVector256Method);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Call(refWordToRefByteMethod);
        method.Call(readVector256Method);

        method.Call(operationUnegenerified);
        method.StoreLocal(locals.lbool);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocal(locals.lbool);
        method.Convert<uint>();
        method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
    }

    internal static void EmitExpInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label powerIsZero = method.DefineLabel(locals.GetLabelName());
        Label baseIsOneOrZero = method.DefineLabel(locals.GetLabelName());
        Label endOfExpImpl = method.DefineLabel(locals.GetLabelName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Duplicate();
        method.Call(Word.LeadingZeroProp);
        method.StoreLocal(locals.uint64A);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocalAddress(locals.uint256B);
        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
        method.BranchIfTrue(powerIsZero);

        // load spec
        method.LoadLocal(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);
        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExpByteCost)));
        method.LoadConstant((long)32);
        method.LoadLocal(locals.uint64A);
        method.Subtract();
        method.Multiply();
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.LoadLocalAddress(locals.uint256A);
        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZeroOrOne)).GetMethod!);
        method.BranchIfTrue(baseIsOneOrZero);

        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256B);
        method.LoadLocalAddress(locals.uint256R);
        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256R);
        method.Call(Word.SetUInt256ByRef);

        method.Branch(endOfExpImpl);

        method.MarkLabel(powerIsZero);
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadConstant(1);
        method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
        method.Branch(endOfExpImpl);

        method.MarkLabel(baseIsOneOrZero);
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.SetUInt256ByRef);
        method.Branch(endOfExpImpl);

        method.MarkLabel(endOfExpImpl);
    }

    internal static void EmitMulModInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label push0Zero = method.DefineLabel(locals.GetLabelName());
        Label fallbackToUInt256Call = method.DefineLabel(locals.GetLabelName());
        Label endofOpcode = method.DefineLabel(locals.GetLabelName());
        // we the two uint256 from the locals.stackHeadRef
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.StoreLocal(locals.wordRef256A);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.StoreLocal(locals.wordRef256B);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.StoreLocal(locals.wordRef256C);

        // since (a * b) % c
        // if a or b are 0 then the result is 0
        // if c is 0 or 1 then the result is 0
        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256A);
        method.BranchIfTrue(push0Zero);
        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
        method.BranchIfTrue(push0Zero);
        method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256C);
        method.BranchIfTrue(push0Zero);

        // since (a * b) % c == (a % c * b % c) % c
        // if a or b are equal to c, then the result is 0
        method.LoadLocal(locals.wordRef256A);
        method.LoadLocal(locals.wordRef256C);
        method.Call(Word.AreEqual);
        method.BranchIfTrue(push0Zero);
        method.LoadLocal(locals.wordRef256B);
        method.LoadLocal(locals.wordRef256C);
        method.Call(Word.AreEqual);
        method.BranchIfTrue(push0Zero);

        method.MarkLabel(fallbackToUInt256Call);
        EmitTrinaryUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.MultiplyMod), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
        method.Branch(endofOpcode);

        method.MarkLabel(push0Zero);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.Branch(endofOpcode);

        method.MarkLabel(endofOpcode);
    }

    internal static void EmitAddModeInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label push0Zero = method.DefineLabel(locals.GetLabelName());
        Label fallbackToUInt256Call = method.DefineLabel(locals.GetLabelName());
        Label endofOpcode = method.DefineLabel(locals.GetLabelName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.StoreLocal(locals.wordRef256C);

        // if c is 1 or 0 result is 0
        method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256C);
        method.BranchIfFalse(fallbackToUInt256Call);

        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.Branch(endofOpcode);

        method.MarkLabel(fallbackToUInt256Call);
        EmitTrinaryUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.AddMod), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
        method.MarkLabel(endofOpcode);
    }

    internal static void EmitSDivInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label fallBackToOldBehavior = method.DefineLabel(locals.GetLabelName());
        Label pushZeroLabel = method.DefineLabel(locals.GetLabelName());
        Label pushALabel = method.DefineLabel(locals.GetLabelName());
        Label endofOpcode = method.DefineLabel(locals.GetLabelName());
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.StoreLocal(locals.wordRef256A);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.StoreLocal(locals.wordRef256B);


        // if b is 0 or a is 0 then the result is 0
        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
        method.BranchIfTrue(pushZeroLabel);
        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256A);
        method.BranchIfTrue(pushZeroLabel);

        // if b is 1 in all cases the result is a
        method.EmitCheck(nameof(Word.IsOne), locals.wordRef256B);
        method.BranchIfTrue(pushALabel);

        // if b is -1 and a is 2^255 then the result is 2^255
        method.EmitCheck(nameof(Word.IsMinusOne), locals.wordRef256B);
        method.BranchIfFalse(fallBackToOldBehavior);

        method.EmitCheck(nameof(Word.IsP255), locals.wordRef256A);
        method.BranchIfTrue(pushALabel);

        method.MarkLabel(fallBackToOldBehavior);
        EmitBinaryInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Divide), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, evmExceptionLabels);
        method.Branch(endofOpcode);

        method.MarkLabel(pushZeroLabel);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Branch(endofOpcode);

        method.MarkLabel(pushALabel);
        method.LoadLocal(locals.wordRef256B);
        method.LoadLocal(locals.wordRef256A);
        method.LoadObject(typeof(Word));
        method.StoreObject(typeof(Word));
        method.Branch(endofOpcode);

        method.MarkLabel(endofOpcode);
    }

    internal static void EmitTStoreInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Call(Word.GetArray);
        method.StoreLocal(locals.localArray);

        envLoader.LoadEnvByRef(method, locals);

        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
        method.LoadLocalAddress(locals.uint256A);
        method.NewObject(typeof(StorageCell), typeof(Address), typeof(UInt256).MakeByRefType());
        method.StoreLocal(locals.storageCell);

        envLoader.LoadWorldState(method, locals);
        method.LoadLocalAddress(locals.storageCell);
        method.LoadLocal(locals.localArray);
        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.SetTransientState), [typeof(StorageCell).MakeByRefType(), typeof(byte[])]));
    }

    internal static void EmitTLoadInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        envLoader.LoadEnvByRef(method, locals);

        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
        method.LoadLocalAddress(locals.uint256A);
        method.NewObject(typeof(StorageCell), typeof(Address), typeof(UInt256).MakeByRefType());
        method.StoreLocal(locals.storageCell);

        envLoader.LoadWorldState(method, locals);
        method.LoadLocalAddress(locals.storageCell);
        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.GetTransientState), [typeof(StorageCell).MakeByRefType()]));
        method.StoreLocal(locals.localReadonOnlySpan);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocal(locals.localReadonOnlySpan);
        method.Call(Word.SetReadOnlySpan);
    }

    internal static void EmitSLoadInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.LoadLocal(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);
        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetSLoadCost)));
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        envLoader.LoadEnvByRef(method, locals);

        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
        method.LoadLocalAddress(locals.uint256A);
        method.NewObject(typeof(StorageCell), typeof(Address), typeof(UInt256).MakeByRefType());
        method.StoreLocal(locals.storageCell);

        method.LoadLocalAddress(locals.gasAvailable);
        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.storageCell);
        method.LoadConstant((int)VirtualMachineDependencies.StorageAccessType.SLOAD);
        envLoader.LoadSpec(method, locals);
        envLoader.LoadTxTracer(method, locals);

        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.ChargeStorageAccessGas), BindingFlags.Static | BindingFlags.Public));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        envLoader.LoadWorldState(method, locals);
        method.LoadLocalAddress(locals.storageCell);
        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.Get), [typeof(StorageCell).MakeByRefType()]));
        method.StoreLocal(locals.localReadonOnlySpan);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocal(locals.localReadonOnlySpan);
        method.Call(Word.SetReadOnlySpan);
    }

    internal static void EmitExtcodeSizeInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.LoadLocal(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);
        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Call(Word.GetAddress);
        method.StoreLocal(locals.address);

        EmitChargeAccountAccessGas(method, envLoader, locals);
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

        EmitGetCachedCodeInfo(method, envLoader, locals);
        method.CallVirtual(GetPropertyInfo<ICodeInfo>(nameof(ICodeInfo.Code), false, out _));
        method.StoreLocal(locals.localReadOnlyMemory);
        method.LoadLocalAddress(locals.localReadOnlyMemory);
        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));

        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
    }

    private static void EmitGetCachedCodeInfo<TDelegateType>(Emit<TDelegateType> method, EnvirementLoader envLoader, Locals<TDelegateType> locals)
    {
        envLoader.LoadCodeInfoRepository(method, locals);
        envLoader.LoadWorldState(method, locals);
        method.LoadLocal(locals.address);
        envLoader.LoadSpec(method, locals);
        method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
    }

    internal static Label EmitExtcodeCopyInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 4);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocal(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);
        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
        method.LoadLocalAddress(locals.uint256C);
        method.LoadLocalAddress(locals.lbool);
        method.Call(typeof(EvmInstructions).GetMethod(nameof(EvmInstructions.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
        method.LoadConstant(GasCostOf.Memory);
        method.Multiply();
        method.Add();
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Call(Word.GetAddress);
        method.StoreLocal(locals.address);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);

        EmitChargeAccountAccessGas(method, envLoader, locals);
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
        method.BranchIfTrue(endOfOpcode);

        envLoader.LoadVmState(method, locals, false);

        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.uint256C);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.UpdateMemoryCost)));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        EmitGetCachedCodeInfo(method, envLoader, locals);
        method.CallVirtual(GetPropertyInfo<ICodeInfo>(nameof(ICodeInfo.Code), false, out _));

        method.LoadLocalAddress(locals.uint256B);
        method.LoadLocal(locals.uint256C);
        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
        method.Convert<int>();
        method.LoadConstant((int)PadDirection.Right);
        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
        method.StoreLocal(locals.localZeroPaddedSpan);

        envLoader.LoadMemoryByRef(method, locals);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.localZeroPaddedSpan);
        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

        method.MarkLabel(endOfOpcode);
        return endOfOpcode;
    }

    internal static void EmitExtcodeHashInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());

        method.LoadLocal(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);
        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeHashCost)));
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Call(Word.GetAddress);
        method.StoreLocal(locals.address);

        EmitChargeAccountAccessGas(method, envLoader, locals);
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        Label pushZeroLabel = method.DefineLabel(locals.GetLabelName());
        Label pushhashcodeLabel = method.DefineLabel(locals.GetLabelName());

        // account exists
        envLoader.LoadWorldState(method, locals);
        method.LoadLocal(locals.address);
        method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.AccountExists)));
        method.BranchIfFalse(pushZeroLabel);

        envLoader.LoadWorldState(method, locals);
        method.LoadLocal(locals.address);
        method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.IsDeadAccount)));
        method.BranchIfTrue(pushZeroLabel);

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        envLoader.LoadCodeInfoRepository(method, locals);
        envLoader.LoadWorldState(method, locals);
        method.LoadLocal(locals.address);
        envLoader.LoadSpec(method, locals);
        method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.GetExecutableCodeHash), [typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
        method.Call(Word.SetKeccak);
        method.Branch(endOfOpcode);

        // Push 0
        method.MarkLabel(pushZeroLabel);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

        method.MarkLabel(endOfOpcode);
    }

    private static void EmitChargeAccountAccessGas<TDelegateType>(Emit<TDelegateType> method, EnvirementLoader envLoader, Locals<TDelegateType> locals)
    {
        // ref long gasAvailable, EvmState vmState, Address address, IReleaseSpec spec, bool chargeForWarm = true
        method.LoadLocalAddress(locals.gasAvailable);
        envLoader.LoadVmState(method, locals, false);
        method.LoadLocal(locals.address);
        envLoader.LoadSpec(method, locals);
        envLoader.LoadTxTracer(method, locals);
        method.LoadConstant(true);
        method.Call(typeof(VirtualMachineDependencies).GetMethod(nameof(VirtualMachineDependencies.ChargeAccountAccessGas)));
    }

    internal static void EmitSelfBalanceInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        envLoader.LoadWorldState(method, locals);
        envLoader.LoadEnvByRef(method, locals);

        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
        method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
        method.Call(Word.SetUInt256ByVal);
    }

    internal static void EmitBalanceInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.LoadLocal(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);
        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetBalanceCost)));
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Call(Word.GetAddress);
        method.StoreLocal(locals.address);

        EmitChargeAccountAccessGas(method, envLoader, locals);
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        envLoader.LoadWorldState(method, locals);
        method.LoadLocal(locals.address);
        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.GetBalance)));
        method.Call(Word.SetUInt256ByRef);
    }

    internal static void EmitDivInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label fallBackToOldBehavior = method.DefineLabel(locals.GetLabelName());
        Label pushZeroLabel = method.DefineLabel(locals.GetLabelName());
        Label pushALabel = method.DefineLabel(locals.GetLabelName());
        Label endofOpcode = method.DefineLabel(locals.GetLabelName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.StoreLocal(locals.wordRef256A);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.StoreLocal(locals.wordRef256B);

        // if a or b are 0 result is directly 0
        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
        method.BranchIfTrue(pushZeroLabel);
        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256A);
        method.BranchIfTrue(pushZeroLabel);

        // if b is 1 result is by default a
        method.EmitCheck(nameof(Word.IsOne), locals.wordRef256B);
        method.BranchIfTrue(pushALabel);

        EmitBinaryUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
        method.Branch(endofOpcode);

        method.MarkLabel(pushZeroLabel);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Branch(endofOpcode);

        method.MarkLabel(pushALabel);
        method.LoadLocal(locals.wordRef256B);
        method.LoadLocal(locals.wordRef256A);
        method.LoadObject(typeof(Word));
        method.StoreObject(typeof(Word));
        method.Branch(endofOpcode);

        method.MarkLabel(endofOpcode);
    }

    internal static void EmitSModInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label bIsOneOrZero = method.DefineLabel(locals.GetLabelName());
        Label endofOpcode = method.DefineLabel(locals.GetLabelName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.StoreLocal(locals.wordRef256B);

        // if b is 1 or 0 result is always 0
        method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256B);
        method.BranchIfTrue(bIsOneOrZero);

        EmitBinaryInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Mod), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, evmExceptionLabels);
        method.Branch(endofOpcode);

        method.MarkLabel(bIsOneOrZero);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.MarkLabel(endofOpcode);
    }

    internal static void EmitModInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label pushZeroLabel = method.DefineLabel(locals.GetLabelName());
        Label fallBackToOldBehavior = method.DefineLabel(locals.GetLabelName());
        Label endofOpcode = method.DefineLabel(locals.GetLabelName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.EmitCheck(nameof(Word.IsZero));
        method.BranchIfTrue(pushZeroLabel);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.EmitCheck(nameof(Word.IsOneOrZero));
        method.BranchIfTrue(pushZeroLabel);

        EmitBinaryUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
        method.Branch(endofOpcode);

        method.MarkLabel(pushZeroLabel);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.MarkLabel(endofOpcode);
    }

    internal static void EmitMulInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label push0Zero = method.DefineLabel(locals.GetLabelName());
        Label pushItemA = method.DefineLabel(locals.GetLabelName());
        Label pushItemB = method.DefineLabel(locals.GetLabelName());
        Label endofOpcode = method.DefineLabel(locals.GetLabelName());
        // we the two uint256 from the locals.stackHeadRef
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.StoreLocal(locals.wordRef256A);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.StoreLocal(locals.wordRef256B);

        method.LoadLocal(locals.wordRef256A);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.LoadLocal(locals.wordRef256B);
        method.LoadLocalAddress(locals.uint256B);
        method.Call(Word.GetUInt256ByRef);

        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256A);
        method.BranchIfTrue(push0Zero);

        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
        method.BranchIfTrue(endofOpcode);

        method.EmitCheck(nameof(Word.IsOne), locals.wordRef256A);
        method.BranchIfTrue(endofOpcode);

        method.EmitCheck(nameof(Word.IsOne), locals.wordRef256B);
        method.BranchIfTrue(pushItemA);

        EmitBinaryUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
        method.Branch(endofOpcode);

        method.MarkLabel(push0Zero);
        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Branch(endofOpcode);

        method.MarkLabel(pushItemA);
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocal(locals.wordRef256A);
        method.LoadObject(typeof(Word));
        method.StoreObject(typeof(Word));

        method.MarkLabel(endofOpcode);
    }

    internal static void EmitSubInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label pushNegItemB = method.DefineLabel(locals.GetLabelName());
        Label pushItemA = method.DefineLabel(locals.GetLabelName());
        // b - a a::b
        Label fallbackToUInt256Call = method.DefineLabel(locals.GetLabelName());
        Label endofOpcode = method.DefineLabel(locals.GetLabelName());
        // we the two uint256 from the locals.stackHeadRef
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.StoreLocal(locals.wordRef256A);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.StoreLocal(locals.wordRef256B);

        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
        method.BranchIfTrue(pushItemA);

        EmitBinaryUInt256Method(method, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
        method.Branch(endofOpcode);

        method.MarkLabel(pushItemA);
        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.LoadLocal(locals.wordRef256A);
        method.LoadObject(typeof(Word));
        method.StoreObject(typeof(Word));

        method.MarkLabel(endofOpcode);
    }

    internal static void EmitPush_bInstructions<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        int length = Math.Min(codeinfo.Code.Length - pc - 1, op - Instruction.PUSH0);
        var immediateBytes = codeinfo.Code.Slice(pc + 1, length).Span;
        if (immediateBytes.IsZero())
            method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        else
        {
            if (length != 32)
            {
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.Convert<nint>();
                method.LoadConstant(32 - length);
                method.Convert<nint>();
                method.Add();
            }
            else
            {
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.Convert<nint>();
            }
            envLoader.LoadMachineCode(method, locals, true);
            method.Convert<nint>();
            method.LoadConstant(pc + 1);
            method.Convert<nint>();
            method.Add();
            method.LoadConstant(length);
            method.CopyBlock();
        }
    }

    internal static void EmiPush_sInstructions<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        if (op is Instruction.PUSH0)
        {
            method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
        }
        else
        {
            int length = Math.Min(codeinfo.Code.Length - pc - 1, op - Instruction.PUSH0);
            var immediateBytes = codeinfo.Code.Slice(pc + 1, length).Span;
            if (immediateBytes.IsZero())
                method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
            else
            {
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.SpecialPushOpcode(op, immediateBytes);
            }
        }
    }

    internal static void EmitNotInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        var refWordToRefByteMethod = GetAsMethodInfo<Word, byte>();
        var readVector256Method = GetReadUnalignedMethodInfo<Vector256<byte>>();
        var writeVector256Method = GetWriteUnalignedMethodInfo<Vector256<byte>>();
        MethodInfo notVector256Method = typeof(Vector256)
            .GetMethod(nameof(Vector256.OnesComplement), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(typeof(byte));

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Call(refWordToRefByteMethod);
        method.Duplicate();
        method.Call(readVector256Method);
        method.Call(notVector256Method);
        method.Call(writeVector256Method);
    }

    internal static void EmitSStoreInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label endOfOpcode = method.DefineLabel(locals.GetLabelName());
        Label metered = method.DefineLabel(locals.GetLabelName());
        Label endOfOpcodeHandling = method.DefineLabel(locals.GetLabelName());

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.LoadLocalAddress(locals.uint256A);
        method.Call(Word.GetUInt256ByRef);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.Call(Word.GetReadOnlySpan);
        method.StoreLocal(locals.localReadonOnlySpan);


        envLoader.LoadSpec(method, locals);
        method.CallVirtual(typeof(IReleaseSpec).GetProperty(nameof(IReleaseSpec.UseNetGasMeteringWithAStipendFix)).GetGetMethod());
        method.BranchIfTrue(metered);

        envLoader.LoadVmState(method, locals, false);
        envLoader.LoadWorldState(method, locals);
        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.localReadonOnlySpan);
        envLoader.LoadSpec(method, locals);
        envLoader.LoadTxTracer(method, locals);


        MethodInfo SStoreMethodUnMetered = typeof(VirtualMachineDependencies)
                    .GetMethod(nameof(VirtualMachineDependencies.InstructionSStoreUnmetered), BindingFlags.Static | BindingFlags.Public);

        method.Call(SStoreMethodUnMetered);
        method.StoreLocal(locals.uint32A);

        method.Branch(endOfOpcodeHandling);

        method.MarkLabel(metered);

        envLoader.LoadVmState(method, locals, false);
        envLoader.LoadWorldState(method, locals);
        method.LoadLocalAddress(locals.gasAvailable);
        method.LoadLocalAddress(locals.uint256A);
        method.LoadLocalAddress(locals.localReadonOnlySpan);
        envLoader.LoadSpec(method, locals);
        envLoader.LoadTxTracer(method, locals);


        MethodInfo SStoreMethodMetered = typeof(VirtualMachineDependencies)
                    .GetMethod(nameof(VirtualMachineDependencies.InstructionSStoreMetered), BindingFlags.Static | BindingFlags.Public);

        method.Call(SStoreMethodMetered);
        method.StoreLocal(locals.uint32A);

        method.MarkLabel(endOfOpcodeHandling);
        method.LoadLocal(locals.uint32A);
        method.LoadConstant((int)EvmExceptionType.None);
        method.BranchIfEqual(endOfOpcode);

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

        method.MarkLabel(endOfOpcode);
    }

    internal static void EmitSelfDestructInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        MethodInfo selfDestruct = typeof(VirtualMachineDependencies)
            .GetMethod(nameof(VirtualMachineDependencies.InstructionSelfDestruct), BindingFlags.Static | BindingFlags.Public);

        Label skipGasDeduction = method.DefineLabel(locals.GetLabelName());
        Label happyPath = method.DefineLabel(locals.GetLabelName());

        envLoader.LoadSpec(method, locals);
        method.CallVirtual(typeof(IReleaseSpec).GetProperty(nameof(IReleaseSpec.UseShanghaiDDosProtection)).GetGetMethod());
        method.BranchIfFalse(skipGasDeduction);

        method.LoadLocal(locals.gasAvailable);
        method.LoadConstant(GasCostOf.SelfDestructEip150);
        method.Subtract();
        method.Duplicate();
        method.StoreLocal(locals.gasAvailable);
        method.LoadConstant((long)0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

        method.MarkLabel(skipGasDeduction);

        envLoader.LoadVmState(method, locals, false);
        envLoader.LoadWorldState(method, locals);
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Call(Word.GetAddress);
        method.LoadLocalAddress(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);
        envLoader.LoadTxTracer(method, locals);

        method.Call(selfDestruct);

        method.StoreLocal(locals.uint32A);
        method.LoadLocal(locals.uint32A);
        method.LoadConstant((int)EvmExceptionType.Stop);
        method.BranchIfEqual(happyPath);

        envLoader.LoadResult(method, locals, true);
        method.Duplicate();
        method.LoadLocal(locals.uint32A);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
        method.LoadConstant((int)ContractState.Failed);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

        method.FakeBranch(escapeLabels.exitLabel);

        method.MarkLabel(happyPath);
        envLoader.LoadResult(method, locals, true);
        method.LoadConstant((int)ContractState.Finished);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
        method.FakeBranch(escapeLabels.returnLabel);
    }

    internal static void EmitCallInstructions<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        MethodInfo callMethod = typeof(VirtualMachineDependencies)
            .GetMethod(nameof(VirtualMachineDependencies.InstructionCall), BindingFlags.Static | BindingFlags.Public);
        using Local toPushToStack = method.DeclareLocal(typeof(UInt256?), locals.GetLocalName());
        using Local newStateToExe = method.DeclareLocal<object>(locals.GetLocalName());

        Label happyPath = method.DefineLabel(locals.GetLabelName());

        envLoader.LoadVmState(method, locals, false);
        envLoader.LoadCodeInfoRepository(method, locals);
        envLoader.LoadWorldState(method, locals);
        method.LoadLocalAddress(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);

        method.LoadConstant((int)op);

        var index = 1;
        // load gasLimit
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
        method.Call(Word.GetUInt256ByVal);

        // load codeSource
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
        method.Call(Word.GetAddress);

        if (op is Instruction.DELEGATECALL)
        {
            envLoader.LoadEnvByRef(method, locals);
            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
        }
        else if (op is Instruction.STATICCALL)
        {
            method.LoadField(typeof(UInt256).GetField(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
        }
        else
        {
            method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
            method.Call(Word.GetUInt256ByVal);
        }
        // load dataoffset
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
        method.Call(Word.GetUInt256ByVal);

        // load datalength
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
        method.Call(Word.GetUInt256ByVal);

        // load outputOffset
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
        method.Call(Word.GetUInt256ByVal);

        // load outputLength
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
        method.Call(Word.GetUInt256ByVal);

        method.LoadLocalAddress(toPushToStack);

        envLoader.LoadReturnDataBuffer(method, locals, true);

        envLoader.LoadTxTracer(method, locals);

        method.LoadLocalAddress(newStateToExe);

        method.Call(callMethod);

        method.StoreLocal(locals.uint32A);
        method.LoadLocal(locals.uint32A);
        method.LoadConstant((int)EvmExceptionType.None);
        method.BranchIfEqual(happyPath);

        envLoader.LoadResult(method, locals, true);
        method.Duplicate();
        method.LoadLocal(locals.uint32A);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
        method.LoadConstant((int)ContractState.Failed);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

        method.FakeBranch(escapeLabels.exitLabel); ;

        method.MarkLabel(happyPath);
        Label hasNoItemsToPush = method.DefineLabel(locals.GetLabelName());

        method.LoadLocalAddress(toPushToStack);
        method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.HasValue)).GetGetMethod());
        method.BranchIfFalse(hasNoItemsToPush);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
        method.LoadLocalAddress(toPushToStack);
        method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.Value)).GetGetMethod());
        method.Call(Word.SetUInt256ByVal);

        method.MarkLabel(hasNoItemsToPush);

        Label skipStateMachineScheduling = method.DefineLabel(locals.GetLabelName());

        method.LoadLocal(newStateToExe);
        method.LoadNull();
        method.BranchIfEqual(skipStateMachineScheduling);

        envLoader.LoadResult(method, locals, true);
        method.Duplicate();
        method.LoadLocal(newStateToExe);
        method.CastClass(typeof(EvmState));
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));
        method.LoadConstant((int)ContractState.Halted);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
        method.Branch(escapeLabels.returnLabel);

        method.MarkLabel(skipStateMachineScheduling);
    }

    internal static void EmitCreateInstructions<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        MethodInfo callMethod = typeof(VirtualMachineDependencies)
            .GetMethod(nameof(VirtualMachineDependencies.InstructionCreate), BindingFlags.Static | BindingFlags.Public)
            .MakeGenericMethod(op == Instruction.CREATE
                ? typeof(EvmInstructions.OpCreate)
                : typeof(EvmInstructions.OpCreate2));

        using Local toPushToStack = method.DeclareLocal(typeof(UInt256?), locals.GetLocalName());
        using Local newStateToExe = method.DeclareLocal<object>(locals.GetLocalName());
        Label happyPath = method.DefineLabel(locals.GetLabelName());

        envLoader.LoadVmState(method, locals, false);
        envLoader.LoadWorldState(method, locals);
        envLoader.LoadCodeInfoRepository(method, locals);
        method.LoadLocalAddress(locals.gasAvailable);
        envLoader.LoadSpec(method, locals);

        int index = 1;

        // TODO: fix loading by value and replace fixing by ref

        // load value
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
        method.Call(Word.GetUInt256ByVal);

        // load memory offset
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
        method.Call(Word.GetUInt256ByVal);

        // load initcode len
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
        method.Call(Word.GetUInt256ByVal);

        // load callvalue
        if (op is Instruction.CREATE2)
        {
            method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
            method.Call(Word.GetMutableSpan);
        }
        else
        {
            // load empty span
            index--;
            method.Call(typeof(Span<byte>).GetProperty(nameof(Span<byte>.Empty), BindingFlags.Static | BindingFlags.Public).GetGetMethod());
        }

        method.LoadLocalAddress(toPushToStack);

        envLoader.LoadReturnDataBuffer(method, locals, true);

        method.LoadLocalAddress(newStateToExe);

        method.Call(callMethod);

        method.StoreLocal(locals.uint32A);

        method.LoadLocal(locals.uint32A);
        method.LoadConstant((int)EvmExceptionType.None);
        method.BranchIfEqual(happyPath);

        envLoader.LoadResult(method, locals, true);
        method.Duplicate();
        method.LoadLocal(locals.uint32A);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
        method.LoadConstant((int)ContractState.Failed);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
        method.FakeBranch(escapeLabels.exitLabel);

        method.MarkLabel(happyPath);
        Label hasNoItemsToPush = method.DefineLabel(locals.GetLabelName());

        method.LoadLocalAddress(toPushToStack);
        method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.HasValue)).GetGetMethod());
        method.BranchIfFalse(hasNoItemsToPush);

        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
        method.LoadLocalAddress(toPushToStack);
        method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.Value)).GetGetMethod());
        method.Call(Word.SetUInt256ByVal);

        method.MarkLabel(hasNoItemsToPush);

        Label skipStateMachineScheduling = method.DefineLabel(locals.GetLabelName());

        method.LoadLocal(newStateToExe);
        method.LoadNull();
        method.BranchIfEqual(skipStateMachineScheduling);

        envLoader.LoadResult(method, locals, true);
        method.Duplicate();
        method.LoadLocal(newStateToExe);
        method.CastClass(typeof(EvmState));
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));
        method.LoadConstant((int)ContractState.Halted);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
        method.Branch(escapeLabels.returnLabel);

        method.MarkLabel(skipStateMachineScheduling);
    }

    internal static void EmitStopInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        envLoader.LoadResult(method, locals, true);
        method.LoadConstant((int)ContractState.Finished);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
        method.FakeBranch(escapeLabels.returnLabel);
    }

    internal static void EmitJumpiInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        Label noJump = method.DefineLabel(locals.GetLabelName());
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
        method.EmitCheck(nameof(Word.IsZero));
        // if the jump condition is false, we do not jump
        method.BranchIfTrue(noJump);

        // we jump into the jump table
        HandleJumpdestination(method, codeinfo, contractMetadata, currentSubSegment, pc, envLoader, locals, evmExceptionLabels, escapeLabels);

        method.MarkLabel(noJump);
    }

    private static void HandleJumpdestination<TDelegateType>(Emit<TDelegateType> method, ICodeInfo codeinfo, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
        method.Duplicate();
        method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
        method.StoreLocal(locals.jmpDestination);

        method.EmitCheck(nameof(Word.IsShort));
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.LoadLocal(locals.jmpDestination);
        method.LoadConstant(0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.LoadLocal(locals.jmpDestination);
        method.LoadConstant(codeinfo.Code.Length);
        method.BranchIfGreaterOrEqual(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        envLoader.LoadResult(method, locals, true);
        method.Duplicate();
        method.LoadConstant((int)ContractState.Jumping);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

        method.LoadLocal(locals.jmpDestination);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.JumpDestination)));
    }

    internal static void EmitJumpInstruction<TDelegateType>(
        Emit<TDelegateType> method, ICodeInfo codeinfo, Instruction op, IVMConfig ilCompilerConfig, ContractCompilerMetadata contractMetadata, SubSegmentMetadata currentSubSegment, int pc, OpcodeMetadata opcodeMetadata, EnvirementLoader envLoader, Locals<TDelegateType> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label exitLabel) escapeLabels)
    {
        HandleJumpdestination(method, codeinfo, contractMetadata, currentSubSegment, pc, envLoader, locals, evmExceptionLabels, escapeLabels);
    }
}
