// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using unsafe OpCode = delegate*<IEvm, ref EvmStack, ref long, ref int, EvmExceptionType>;
using Int256;

internal unsafe sealed partial class EvmInstructions
{
    public static OpCode[] GenerateOpCodes(IReleaseSpec spec)
    {
        var lookup = new delegate*<IEvm, ref EvmStack, ref long, ref int, EvmExceptionType>[256];

        for (int i = 0; i < lookup.Length; i++)
        {
            lookup[i] = &InstructionBadInstruction;
        }

        lookup[(int)Instruction.STOP] = &InstructionStop;
        lookup[(int)Instruction.ADD] = &InstructionMath2Param<OpAdd>;
        lookup[(int)Instruction.MUL] = &InstructionMath2Param<OpMul>;
        lookup[(int)Instruction.SUB] = &InstructionMath2Param<OpSub>;
        lookup[(int)Instruction.DIV] = &InstructionMath2Param<OpDiv>;
        lookup[(int)Instruction.SDIV] = &InstructionMath2Param<OpSDiv>;
        lookup[(int)Instruction.MOD] = &InstructionMath2Param<OpMod>;
        lookup[(int)Instruction.SMOD] = &InstructionMath2Param<OpSMod>;
        lookup[(int)Instruction.ADDMOD] = &InstructionMath3Param<OpAddMod>;
        lookup[(int)Instruction.MULMOD] = &InstructionMath3Param<OpMulMod>;
        lookup[(int)Instruction.EXP] = &InstructionExp;
        lookup[(int)Instruction.SIGNEXTEND] = &InstructionSignExtend;
        lookup[(int)Instruction.LT] = &InstructionMath2Param<OpLt>;
        lookup[(int)Instruction.GT] = &InstructionMath2Param<OpGt>;
        lookup[(int)Instruction.SLT] = &InstructionMath2Param<OpSLt>;
        lookup[(int)Instruction.SGT] = &InstructionMath2Param<OpSGt>;
        lookup[(int)Instruction.EQ] = &InstructionBitwise<OpBitwiseEq>;
        lookup[(int)Instruction.ISZERO] = &InstructionMath1Param<OpIsZero>;
        lookup[(int)Instruction.AND] = &InstructionBitwise<OpBitwiseAnd>;
        lookup[(int)Instruction.OR] = &InstructionBitwise<OpBitwiseOr>;
        lookup[(int)Instruction.XOR] = &InstructionBitwise<OpBitwiseXor>;
        lookup[(int)Instruction.NOT] = &InstructionMath1Param<OpNot>;
        lookup[(int)Instruction.BYTE] = &InstructionByte;
        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = &InstructionShift<OpShl>;
            lookup[(int)Instruction.SHR] = &InstructionShift<OpShr>;
            lookup[(int)Instruction.SAR] = &InstructionSar;
        }

        lookup[(int)Instruction.KECCAK256] = &InstructionKeccak256;

        lookup[(int)Instruction.ADDRESS] = &InstructionEnvBytes<OpAddress>;
        lookup[(int)Instruction.BALANCE] = &InstructionBalance;
        lookup[(int)Instruction.ORIGIN] = &InstructionEnvBytes<OpOrigin>;
        lookup[(int)Instruction.CALLER] = &InstructionEnvBytes<OpCaller>;
        lookup[(int)Instruction.CALLVALUE] = &InstructionEnvUInt256<OpCallValue>;
        lookup[(int)Instruction.CALLDATALOAD] = &InstructionCallDataLoad;
        lookup[(int)Instruction.CALLDATASIZE] = &InstructionEnvUInt256<OpCallDataSize>;
        lookup[(int)Instruction.CALLDATACOPY] = &InstructionCodeCopy<OpCallDataCopy>;
        lookup[(int)Instruction.CODESIZE] = &InstructionEnvUInt256<OpCodeSize>;
        lookup[(int)Instruction.CODECOPY] = &InstructionCodeCopy<OpCodeCopy>;
        lookup[(int)Instruction.GASPRICE] = &InstructionEnvUInt256<OpGasPrice>;

        lookup[(int)Instruction.EXTCODESIZE] = &InstructionExtCodeSize;

        lookup[(int)Instruction.EXTCODECOPY] = &InstructionExtCodeCopy;

        if (spec.ReturnDataOpcodesEnabled)
        {
            lookup[(int)Instruction.RETURNDATASIZE] = &InstructionReturnDataSize;
            lookup[(int)Instruction.RETURNDATACOPY] = &InstructionReturnDataCopy;
        }

        lookup[(int)Instruction.EXTCODEHASH] = &InstructionExtCodeHash;

        lookup[(int)Instruction.BLOCKHASH] = &InstructionBlockHash;

        lookup[(int)Instruction.COINBASE] = &InstructionEnvBytes<OpCoinbase>;
        lookup[(int)Instruction.TIMESTAMP] = &InstructionEnvUInt256<OpTimestamp>;
        lookup[(int)Instruction.NUMBER] = &InstructionEnvUInt256<OpNumber>;
        lookup[(int)Instruction.PREVRANDAO] = &InstructionPrevRandao;
        lookup[(int)Instruction.GASLIMIT] = &InstructionEnvUInt256<OpGasLimit>;
        lookup[(int)Instruction.CHAINID] = &InstructionChainId;

        if (spec.SelfBalanceOpcodeEnabled)
        {
            lookup[(int)Instruction.SELFBALANCE] = &InstructionSelfBalance;
        }

        lookup[(int)Instruction.BASEFEE] = &InstructionEnvUInt256<OpBaseFee>;
        lookup[(int)Instruction.BLOBHASH] = &InstructionBlobHash;
        lookup[(int)Instruction.BLOBBASEFEE] = &InstructionEnvUInt256<OpBlobBaseFee>;
        // Gap: 0x4b to 0x4f
        lookup[(int)Instruction.POP] = &InstructionPop;
        lookup[(int)Instruction.MLOAD] = &InstructionMLoad;
        lookup[(int)Instruction.MSTORE] = &InstructionMStore;
        lookup[(int)Instruction.MSTORE8] = &InstructionMStore8;
        lookup[(int)Instruction.SLOAD] = &InstructionSLoad;
        lookup[(int)Instruction.SSTORE] = &InstructionSStore;
        lookup[(int)Instruction.JUMP] = &InstructionJump;
        lookup[(int)Instruction.JUMPI] = &InstructionJumpIf;
        lookup[(int)Instruction.PC] = &InstructionProgramCounter;
        lookup[(int)Instruction.MSIZE] = &InstructionEnvUInt256<OpMSize>;
        lookup[(int)Instruction.GAS] = &InstructionGas;
        lookup[(int)Instruction.JUMPDEST] = &InstructionJumpDest;

        if (spec.TransientStorageEnabled)
        {
            lookup[(int)Instruction.TLOAD] = &InstructionTLoad;
            lookup[(int)Instruction.TSTORE] = &InstructionTStore;
        }
        if (spec.MCopyIncluded)
        {
            lookup[(int)Instruction.MCOPY] = &InstructionMCopy;
        }

        if (spec.IncludePush0Instruction)
        {
            lookup[(int)Instruction.PUSH0] = &InstructionPush0;
        }

        lookup[(int)Instruction.PUSH1] = &InstructionPush<Op1>;
        lookup[(int)Instruction.PUSH2] = &InstructionPush<Op2>;
        lookup[(int)Instruction.PUSH3] = &InstructionPush<Op3>;
        lookup[(int)Instruction.PUSH4] = &InstructionPush<Op4>;
        lookup[(int)Instruction.PUSH5] = &InstructionPush<Op5>;
        lookup[(int)Instruction.PUSH6] = &InstructionPush<Op6>;
        lookup[(int)Instruction.PUSH7] = &InstructionPush<Op7>;
        lookup[(int)Instruction.PUSH8] = &InstructionPush<Op8>;
        lookup[(int)Instruction.PUSH9] = &InstructionPush<Op9>;
        lookup[(int)Instruction.PUSH10] = &InstructionPush<Op10>;
        lookup[(int)Instruction.PUSH11] = &InstructionPush<Op11>;
        lookup[(int)Instruction.PUSH12] = &InstructionPush<Op12>;
        lookup[(int)Instruction.PUSH13] = &InstructionPush<Op13>;
        lookup[(int)Instruction.PUSH14] = &InstructionPush<Op14>;
        lookup[(int)Instruction.PUSH15] = &InstructionPush<Op15>;
        lookup[(int)Instruction.PUSH16] = &InstructionPush<Op16>;
        lookup[(int)Instruction.PUSH17] = &InstructionPush<Op17>;
        lookup[(int)Instruction.PUSH18] = &InstructionPush<Op18>;
        lookup[(int)Instruction.PUSH19] = &InstructionPush<Op19>;
        lookup[(int)Instruction.PUSH20] = &InstructionPush<Op20>;
        lookup[(int)Instruction.PUSH21] = &InstructionPush<Op21>;
        lookup[(int)Instruction.PUSH22] = &InstructionPush<Op22>;
        lookup[(int)Instruction.PUSH23] = &InstructionPush<Op23>;
        lookup[(int)Instruction.PUSH24] = &InstructionPush<Op24>;
        lookup[(int)Instruction.PUSH25] = &InstructionPush<Op25>;
        lookup[(int)Instruction.PUSH26] = &InstructionPush<Op26>;
        lookup[(int)Instruction.PUSH27] = &InstructionPush<Op27>;
        lookup[(int)Instruction.PUSH28] = &InstructionPush<Op28>;
        lookup[(int)Instruction.PUSH29] = &InstructionPush<Op29>;
        lookup[(int)Instruction.PUSH30] = &InstructionPush<Op30>;
        lookup[(int)Instruction.PUSH31] = &InstructionPush<Op31>;
        lookup[(int)Instruction.PUSH32] = &InstructionPush<Op32>;

        lookup[(int)Instruction.DUP1] = &InstructionDup<Op1>;
        lookup[(int)Instruction.DUP2] = &InstructionDup<Op2>;
        lookup[(int)Instruction.DUP3] = &InstructionDup<Op3>;
        lookup[(int)Instruction.DUP4] = &InstructionDup<Op4>;
        lookup[(int)Instruction.DUP5] = &InstructionDup<Op5>;
        lookup[(int)Instruction.DUP6] = &InstructionDup<Op6>;
        lookup[(int)Instruction.DUP7] = &InstructionDup<Op7>;
        lookup[(int)Instruction.DUP8] = &InstructionDup<Op8>;
        lookup[(int)Instruction.DUP9] = &InstructionDup<Op9>;
        lookup[(int)Instruction.DUP10] = &InstructionDup<Op10>;
        lookup[(int)Instruction.DUP11] = &InstructionDup<Op11>;
        lookup[(int)Instruction.DUP12] = &InstructionDup<Op12>;
        lookup[(int)Instruction.DUP13] = &InstructionDup<Op13>;
        lookup[(int)Instruction.DUP14] = &InstructionDup<Op14>;
        lookup[(int)Instruction.DUP15] = &InstructionDup<Op15>;
        lookup[(int)Instruction.DUP16] = &InstructionDup<Op16>;

        lookup[(int)Instruction.SWAP1] = &InstructionSwap<Op1>;
        lookup[(int)Instruction.SWAP2] = &InstructionSwap<Op2>;
        lookup[(int)Instruction.SWAP3] = &InstructionSwap<Op3>;
        lookup[(int)Instruction.SWAP4] = &InstructionSwap<Op4>;
        lookup[(int)Instruction.SWAP5] = &InstructionSwap<Op5>;
        lookup[(int)Instruction.SWAP6] = &InstructionSwap<Op6>;
        lookup[(int)Instruction.SWAP7] = &InstructionSwap<Op7>;
        lookup[(int)Instruction.SWAP8] = &InstructionSwap<Op8>;
        lookup[(int)Instruction.SWAP9] = &InstructionSwap<Op9>;
        lookup[(int)Instruction.SWAP10] = &InstructionSwap<Op10>;
        lookup[(int)Instruction.SWAP11] = &InstructionSwap<Op11>;
        lookup[(int)Instruction.SWAP12] = &InstructionSwap<Op12>;
        lookup[(int)Instruction.SWAP13] = &InstructionSwap<Op13>;
        lookup[(int)Instruction.SWAP14] = &InstructionSwap<Op14>;
        lookup[(int)Instruction.SWAP15] = &InstructionSwap<Op15>;
        lookup[(int)Instruction.SWAP16] = &InstructionSwap<Op16>;

        lookup[(int)Instruction.LOG0] = &InstructionLog<Op0>;
        lookup[(int)Instruction.LOG1] = &InstructionLog<Op1>;
        lookup[(int)Instruction.LOG2] = &InstructionLog<Op2>;
        lookup[(int)Instruction.LOG3] = &InstructionLog<Op3>;
        lookup[(int)Instruction.LOG4] = &InstructionLog<Op4>;

        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.DATALOAD] = &InstructionDataLoad;
            lookup[(int)Instruction.DATALOADN] = &InstructionDataLoadN;
            lookup[(int)Instruction.DATASIZE] = &InstructionDataSize;
            lookup[(int)Instruction.DATACOPY] = &InstructionDataCopy;
            lookup[(int)Instruction.RJUMP] = &InstructionRelativeJump;
            lookup[(int)Instruction.RJUMPI] = &InstructionRelativeJumpIf;
            lookup[(int)Instruction.RJUMPV] = &InstructionJumpTable;
            lookup[(int)Instruction.CALLF] = &InstructionCallFunction;
            lookup[(int)Instruction.RETF] = &InstructionReturnFunction;
            lookup[(int)Instruction.JUMPF] = &InstructionJumpFunction;
            lookup[(int)Instruction.DUPN] = &InstructionDupN;
            lookup[(int)Instruction.SWAPN] = &InstructionSwapN;
            lookup[(int)Instruction.EXCHANGE] = &InstructionExchange;
            lookup[(int)Instruction.EOFCREATE] = &InstructionEofCreate;
            lookup[(int)Instruction.RETURNCONTRACT] = &InstructionReturnContract;
        }

        lookup[(int)Instruction.CREATE] = &InstructionCreate<OpCreate>;
        lookup[(int)Instruction.CALL] = &InstructionCall<OpCall>;
        lookup[(int)Instruction.CALLCODE] = &InstructionCall<OpCallCode>;
        lookup[(int)Instruction.RETURN] = &InstructionReturn;
        if (spec.DelegateCallEnabled)
        {
            lookup[(int)Instruction.DELEGATECALL] = &InstructionCall<OpDelegateCall>;
        }
        if (spec.Create2OpcodeEnabled)
        {
            lookup[(int)Instruction.CREATE2] = &InstructionCreate<OpCreate2>;
        }

        lookup[(int)Instruction.RETURNDATALOAD] = &InstructionReturnDataLoad;
        if (spec.StaticCallEnabled)
        {
            lookup[(int)Instruction.STATICCALL] = &InstructionCall<OpStaticCall>;
        }

        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.EXTCALL] = &InstructionEofCall<OpEofCall>;
            if (spec.DelegateCallEnabled)
            {
                lookup[(int)Instruction.EXTDELEGATECALL] = &InstructionEofCall<OpEofDelegateCall>;
            }
            if (spec.StaticCallEnabled)
            {
                lookup[(int)Instruction.EXTSTATICCALL] = &InstructionEofCall<OpEofStaticCall>;
            }
        }

        if (spec.RevertOpcodeEnabled)
        {
            lookup[(int)Instruction.REVERT] = &InstructionRevert;
        }

        lookup[(int)Instruction.INVALID] = &InstructionInvalid;
        lookup[(int)Instruction.SELFDESTRUCT] = &InstructionSelfDestruct;

        return lookup;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionStop(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (vm.State.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
        {
            return EvmExceptionType.BadInstruction;
        }

        return EvmExceptionType.Stop;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionRevert(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            return EvmExceptionType.StackUnderflow;

        if (!UpdateMemoryCost(vm.State, ref gasAvailable, in position, in length))
        {
            return EvmExceptionType.OutOfGas;
        }

        vm.ReturnData = vm.State.Memory.Load(in position, in length).ToArray();

        return EvmExceptionType.Revert;
    }

    [SkipLocalsInit]
    private static EvmExceptionType InstructionSelfDestruct(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        Metrics.SelfDestructs++;

        EvmState vmState = vm.State;
        var spec = vm.Spec;
        var state = vm.WorldState;

        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        if (spec.UseShanghaiDDosProtection)
        {
            gasAvailable -= GasCostOf.SelfDestructEip150;
        }

        Address inheritor = stack.PopAddress();
        if (inheritor is null) return EvmExceptionType.StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, inheritor, false)) return EvmExceptionType.OutOfGas;

        Address executingAccount = vmState.Env.ExecutingAccount;
        bool createInSameTx = vmState.CreateList.Contains(executingAccount);
        if (!spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
            vmState.DestroyList.Add(executingAccount);

        UInt256 result = state.GetBalance(executingAccount);
        if (vm.TxTracer.IsTracingActions) vm.TxTracer.ReportSelfDestruct(executingAccount, result, inheritor);
        if (spec.ClearEmptyAccountWhenTouched && !result.IsZero && state.IsDeadAccount(inheritor))
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        bool inheritorAccountExists = state.AccountExists(inheritor);
        if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        if (!inheritorAccountExists)
        {
            state.CreateAccount(inheritor, result);
        }
        else if (!inheritor.Equals(executingAccount))
        {
            state.AddToBalance(inheritor, result, spec);
        }

        if (spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
            return EvmExceptionType.Stop; // don't burn eth when contract is not destroyed per EIP clarification

        state.SubtractFromBalance(executingAccount, result, spec);
        return EvmExceptionType.Stop;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionPrevRandao(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;
        BlockHeader header = vm.State.Env.TxExecutionContext.BlockExecutionContext.Header;
        if (header.IsPostMerge)
        {
            stack.PushBytes(header.Random.Bytes);
        }
        else
        {
            UInt256 result = header.Difficulty;
            stack.PushUInt256(in result);
        }

        return EvmExceptionType.None;
    }

    public static EvmExceptionType InstructionInvalid(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.High;
        return EvmExceptionType.BadInstruction;
    }

    public static EvmExceptionType InstructionBadInstruction(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        => EvmExceptionType.BadInstruction;

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExp(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Exp;

        if (!stack.PopUInt256(out var a)) return EvmExceptionType.StackUnderflow;
        Span<byte> bytes = stack.PopWord256();

        int leadingZeros = bytes.LeadingZerosCount();
        if (leadingZeros == 32)
        {
            stack.PushOne();
        }
        else
        {
            int expSize = 32 - leadingZeros;
            gasAvailable -= vm.Spec.GetExpByteCost() * expSize;

            if (a.IsZero)
            {
                stack.PushZero();
            }
            else if (a.IsOne)
            {
                stack.PushOne();
            }
            else
            {
                UInt256.Exp(a, new UInt256(bytes, true), out UInt256 result);
                stack.PushUInt256(in result);
            }
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionByte(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out var a)) return EvmExceptionType.StackUnderflow;
        Span<byte> bytes = stack.PopWord256();

        if (a >= BigInt32)
        {
            stack.PushZero();
        }
        else
        {
            int adjustedPosition = bytes.Length - 32 + (int)a;
            if (adjustedPosition < 0)
            {
                stack.PushZero();
            }
            else
            {
                stack.PushByte(bytes[adjustedPosition]);
            }
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSignExtend(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Low;

        if (!stack.PopUInt256(out var a)) return EvmExceptionType.StackUnderflow;
        if (a >= BigInt32)
        {
            if (!stack.EnsureDepth(1)) return EvmExceptionType.StackUnderflow;
            return EvmExceptionType.None;
        }

        int position = 31 - (int)a;

        Span<byte> bytes = stack.PeekWord256();
        sbyte sign = (sbyte)bytes[position];

        if (sign >= 0)
        {
            BytesZero32.AsSpan(0, position).CopyTo(bytes[..position]);
        }
        else
        {
            BytesMax32.AsSpan(0, position).CopyTo(bytes[..position]);
        }

        // Didn't remove from stack so don't need to push back
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionKeccak256(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        gasAvailable -= GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in b);

        EvmState vmState = vm.State;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, b)) return EvmExceptionType.OutOfGas;

        Span<byte> bytes = vmState.Memory.LoadSpan(in a, b);
        stack.PushBytes(ValueKeccak.Compute(bytes).BytesAsSpan);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCallDataLoad(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        stack.PushBytes(vm.State.Env.InputData.SliceWithZeroPadding(result, 32));

        return EvmExceptionType.None;
    }

    public static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length);
        if (memoryCost != 0L)
        {
            if (!UpdateGas(memoryCost, ref gasAvailable))
            {
                return false;
            }
        }

        return true;
    }

    public static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost)
        {
            return false;
        }

        gasAvailable -= gasCost;
        return true;
    }

    public static void UpdateGasUp(long refund, ref long gasAvailable)
    {
        gasAvailable += refund;
    }
}
