// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.State;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using unsafe OpCode = delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType>;
using Int256;

internal unsafe sealed partial class EvmInstructions
{
    /// <summary>
    /// Generates the opcode lookup table for the Ethereum Virtual Machine.
    /// Each of the 256 entries in the returned array corresponds to an EVM instruction,
    /// with unassigned opcodes defaulting to a bad instruction handler.
    /// </summary>
    /// <typeparam name="TTracingInstructions">A struct implementing IFlag used for tracing purposes.</typeparam>
    /// <param name="spec">The release specification containing enabled features and opcode flags.</param>
    /// <returns>An array of function pointers (opcode handlers) indexed by opcode value.</returns>
    public static OpCode[] GenerateOpCodes<TTracingInstructions>(IReleaseSpec spec)
        where TTracingInstructions : struct, IFlag
    {
        // Allocate lookup table for all possible opcodes.
        var lookup = new delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType>[256];

        for (int i = 0; i < lookup.Length; i++)
        {
            lookup[i] = &InstructionBadInstruction;
        }

        // Set basic control and arithmetic opcodes.
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

        // Comparison and bitwise opcodes.
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

        // Conditional: enable shift opcodes if the spec allows.
        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = &InstructionShift<OpShl>;
            lookup[(int)Instruction.SHR] = &InstructionShift<OpShr>;
            lookup[(int)Instruction.SAR] = &InstructionSar;
        }

        // Cryptographic hash opcode.
        lookup[(int)Instruction.KECCAK256] = &InstructionKeccak256;

        // Environment opcodes.
        lookup[(int)Instruction.ADDRESS] = &InstructionEnvBytes<OpAddress>;
        lookup[(int)Instruction.BALANCE] = &InstructionBalance;
        lookup[(int)Instruction.ORIGIN] = &InstructionEnvBytes<OpOrigin>;
        lookup[(int)Instruction.CALLER] = &InstructionEnvBytes<OpCaller>;
        lookup[(int)Instruction.CALLVALUE] = &InstructionEnvUInt256<OpCallValue>;
        lookup[(int)Instruction.CALLDATALOAD] = &InstructionCallDataLoad;
        lookup[(int)Instruction.CALLDATASIZE] = &InstructionEnvUInt32<OpCallDataSize>;
        lookup[(int)Instruction.CALLDATACOPY] = &InstructionCodeCopy<OpCallDataCopy, TTracingInstructions>;
        lookup[(int)Instruction.CODESIZE] = &InstructionEnvUInt32<OpCodeSize>;
        lookup[(int)Instruction.CODECOPY] = &InstructionCodeCopy<OpCodeCopy, TTracingInstructions>;
        lookup[(int)Instruction.GASPRICE] = &InstructionEnvUInt256<OpGasPrice>;

        lookup[(int)Instruction.EXTCODESIZE] = &InstructionExtCodeSize<TTracingInstructions>;
        lookup[(int)Instruction.EXTCODECOPY] = &InstructionExtCodeCopy<TTracingInstructions>;

        // Return data opcodes (if enabled).
        if (spec.ReturnDataOpcodesEnabled)
        {
            lookup[(int)Instruction.RETURNDATASIZE] = &InstructionReturnDataSize;
            lookup[(int)Instruction.RETURNDATACOPY] = &InstructionReturnDataCopy<TTracingInstructions>;
        }

        // Extended code hash opcode handling.
        if (spec.ExtCodeHashOpcodeEnabled)
        {
            lookup[(int)Instruction.EXTCODEHASH] = spec.IsEofEnabled ? &InstructionExtCodeHashEof : &InstructionExtCodeHash;
        }

        lookup[(int)Instruction.BLOCKHASH] = &InstructionBlockHash;

        // More environment opcodes.
        lookup[(int)Instruction.COINBASE] = &InstructionEnvBytes<OpCoinbase>;
        lookup[(int)Instruction.TIMESTAMP] = &InstructionEnvUInt64<OpTimestamp>;
        lookup[(int)Instruction.NUMBER] = &InstructionEnvUInt64<OpNumber>;
        lookup[(int)Instruction.PREVRANDAO] = &InstructionPrevRandao;
        lookup[(int)Instruction.GASLIMIT] = &InstructionEnvUInt64<OpGasLimit>;
        if (spec.ChainIdOpcodeEnabled)
        {
            lookup[(int)Instruction.CHAINID] = &InstructionChainId;
        }
        if (spec.SelfBalanceOpcodeEnabled)
        {
            lookup[(int)Instruction.SELFBALANCE] = &InstructionSelfBalance;
        }
        if (spec.BaseFeeEnabled)
        {
            lookup[(int)Instruction.BASEFEE] = &InstructionEnvUInt256<OpBaseFee>;
        }
        if (spec.IsEip4844Enabled)
        {
            lookup[(int)Instruction.BLOBHASH] = &InstructionBlobHash;
        }
        if (spec.BlobBaseFeeEnabled)
        {
            lookup[(int)Instruction.BLOBBASEFEE] = &InstructionEnvUInt256<OpBlobBaseFee>;
        }

        // Gap: opcodes 0x4b to 0x4f are unassigned.

        // Memory and storage instructions.
        lookup[(int)Instruction.POP] = &InstructionPop;
        lookup[(int)Instruction.MLOAD] = &InstructionMLoad<TTracingInstructions>;
        lookup[(int)Instruction.MSTORE] = &InstructionMStore<TTracingInstructions>;
        lookup[(int)Instruction.MSTORE8] = &InstructionMStore8<TTracingInstructions>;
        lookup[(int)Instruction.SLOAD] = &InstructionSLoad;
        lookup[(int)Instruction.SSTORE] = &InstructionSStore<TTracingInstructions>;

        // Jump instructions.
        lookup[(int)Instruction.JUMP] = &InstructionJump;
        lookup[(int)Instruction.JUMPI] = &InstructionJumpIf;
        lookup[(int)Instruction.PC] = &InstructionProgramCounter;
        lookup[(int)Instruction.MSIZE] = &InstructionEnvUInt64<OpMSize>;
        lookup[(int)Instruction.GAS] = &InstructionGas;
        lookup[(int)Instruction.JUMPDEST] = &InstructionJumpDest;

        // Transient storage opcodes.
        if (spec.TransientStorageEnabled)
        {
            lookup[(int)Instruction.TLOAD] = &InstructionTLoad;
            lookup[(int)Instruction.TSTORE] = &InstructionTStore;
        }
        if (spec.MCopyIncluded)
        {
            lookup[(int)Instruction.MCOPY] = &InstructionMCopy<TTracingInstructions>;
        }

        // Optional PUSH0 instruction.
        if (spec.IncludePush0Instruction)
        {
            lookup[(int)Instruction.PUSH0] = &InstructionPush0;
        }

        // PUSH opcodes (PUSH1 to PUSH32).
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

        // DUP opcodes (DUP1 to DUP16).
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

        // SWAP opcodes (SWAP1 to SWAP16).
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

        // LOG opcodes.
        lookup[(int)Instruction.LOG0] = &InstructionLog<Op0>;
        lookup[(int)Instruction.LOG1] = &InstructionLog<Op1>;
        lookup[(int)Instruction.LOG2] = &InstructionLog<Op2>;
        lookup[(int)Instruction.LOG3] = &InstructionLog<Op3>;
        lookup[(int)Instruction.LOG4] = &InstructionLog<Op4>;

        // Extended opcodes for EO (EoF) mode.
        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.DATALOAD] = &InstructionDataLoad;
            lookup[(int)Instruction.DATALOADN] = &InstructionDataLoadN;
            lookup[(int)Instruction.DATASIZE] = &InstructionDataSize;
            lookup[(int)Instruction.DATACOPY] = &InstructionDataCopy<TTracingInstructions>;
            lookup[(int)Instruction.RJUMP] = &InstructionRelativeJump;
            lookup[(int)Instruction.RJUMPI] = &InstructionRelativeJumpIf;
            lookup[(int)Instruction.RJUMPV] = &InstructionJumpTable;
            lookup[(int)Instruction.CALLF] = &InstructionCallFunction;
            lookup[(int)Instruction.RETF] = &InstructionReturnFunction;
            lookup[(int)Instruction.JUMPF] = &InstructionJumpFunction;
            lookup[(int)Instruction.DUPN] = &InstructionDupN;
            lookup[(int)Instruction.SWAPN] = &InstructionSwapN;
            lookup[(int)Instruction.EXCHANGE] = &InstructionExchange;
            lookup[(int)Instruction.EOFCREATE] = &InstructionEofCreate<TTracingInstructions>;
            lookup[(int)Instruction.RETURNCONTRACT] = &InstructionReturnContract;
        }

        // Contract creation and call opcodes.
        lookup[(int)Instruction.CREATE] = &InstructionCreate<OpCreate, TTracingInstructions>;
        lookup[(int)Instruction.CALL] = &InstructionCall<OpCall, TTracingInstructions>;
        lookup[(int)Instruction.CALLCODE] = &InstructionCall<OpCallCode, TTracingInstructions>;
        lookup[(int)Instruction.RETURN] = &InstructionReturn;
        if (spec.DelegateCallEnabled)
        {
            lookup[(int)Instruction.DELEGATECALL] = &InstructionCall<OpDelegateCall, TTracingInstructions>;
        }
        if (spec.Create2OpcodeEnabled)
        {
            lookup[(int)Instruction.CREATE2] = &InstructionCreate<OpCreate2, TTracingInstructions>;
        }

        lookup[(int)Instruction.RETURNDATALOAD] = &InstructionReturnDataLoad;
        if (spec.StaticCallEnabled)
        {
            lookup[(int)Instruction.STATICCALL] = &InstructionCall<OpStaticCall, TTracingInstructions>;
        }

        // Extended call opcodes in EO mode.
        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.EXTCALL] = &InstructionEofCall<OpEofCall, TTracingInstructions>;
            if (spec.DelegateCallEnabled)
            {
                lookup[(int)Instruction.EXTDELEGATECALL] = &InstructionEofCall<OpEofDelegateCall, TTracingInstructions>;
            }
            if (spec.StaticCallEnabled)
            {
                lookup[(int)Instruction.EXTSTATICCALL] = &InstructionEofCall<OpEofStaticCall, TTracingInstructions>;
            }
        }

        if (spec.RevertOpcodeEnabled)
        {
            lookup[(int)Instruction.REVERT] = &InstructionRevert;
        }

        // Final opcodes.
        lookup[(int)Instruction.INVALID] = &InstructionInvalid;
        lookup[(int)Instruction.SELFDESTRUCT] = &InstructionSelfDestruct;

        return lookup;
    }

    /// <summary>
    /// Stops the execution of the EVM.
    /// In EOFCREATE or TXCREATE executions, the STOP opcode is considered illegal.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionStop(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // In contract creation contexts, a STOP is not permitted.
        if (vm.EvmState.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
        {
            return EvmExceptionType.BadInstruction;
        }

        return EvmExceptionType.Stop;
    }

    /// <summary>
    /// Implements the REVERT opcode.
    /// Pops a memory offset and length from the stack, updates memory gas cost, loads the return data,
    /// and returns a revert exception.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionRevert(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Attempt to pop memory offset and length; if either fails, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
        {
            goto StackUnderflow;
        }

        // Ensure sufficient gas for any required memory expansion.
        if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in position, in length))
        {
            goto OutOfGas;
        }

        // Copy the specified memory region as return data.
        vm.ReturnData = vm.EvmState.Memory.Load(in position, in length).ToArray();

        return EvmExceptionType.Revert;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes the SELFDESTRUCT opcode.
    /// This method handles gas adjustments, account balance transfers,
    /// and marks the executing account for destruction.
    /// </summary>
    [SkipLocalsInit]
    private static EvmExceptionType InstructionSelfDestruct(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Increment metrics for self-destruct operations.
        Metrics.IncrementSelfDestructs();

        EvmState vmState = vm.EvmState;
        IReleaseSpec spec = vm.Spec;
        IWorldState state = vm.WorldState;

        // SELFDESTRUCT is forbidden during static calls.
        if (vmState.IsStatic)
            goto StaticCallViolation;

        // If Shanghai DDoS protection is active, charge the appropriate gas cost.
        if (spec.UseShanghaiDDosProtection)
        {
            gasAvailable -= GasCostOf.SelfDestructEip150;
        }

        // Pop the inheritor address from the stack; signal underflow if missing.
        Address inheritor = stack.PopAddress();
        if (inheritor is null)
            goto StackUnderflow;

        // Charge gas for account access; if insufficient, signal out-of-gas.
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, inheritor, chargeForWarm: false))
            goto OutOfGas;

        Address executingAccount = vmState.Env.ExecutingAccount;
        bool createInSameTx = vmState.AccessTracker.CreateList.Contains(executingAccount);
        // Mark the executing account for destruction if allowed.
        if (!spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
            vmState.AccessTracker.ToBeDestroyed(executingAccount);

        // Retrieve the current balance for transfer.
        UInt256 result = state.GetBalance(executingAccount);
        if (vm.TxTracer.IsTracingActions)
            vm.TxTracer.ReportSelfDestruct(executingAccount, result, inheritor);

        // For certain specs, charge gas if transferring to a dead account.
        if (spec.ClearEmptyAccountWhenTouched && !result.IsZero && state.IsDeadAccount(inheritor))
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                goto OutOfGas;
        }

        // If account creation rules apply, ensure gas is charged for new accounts.
        bool inheritorAccountExists = state.AccountExists(inheritor);
        if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                goto OutOfGas;
        }

        // Create or update the inheritor account with the transferred balance.
        if (!inheritorAccountExists)
        {
            state.CreateAccount(inheritor, result);
        }
        else if (!inheritor.Equals(executingAccount))
        {
            state.AddToBalance(inheritor, result, spec);
        }

        // Special handling when SELFDESTRUCT is limited to the same transaction.
        if (spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
            goto Stop; // Avoid burning ETH if contract is not destroyed per EIP clarification

        // Subtract the balance from the executing account.
        state.SubtractFromBalance(executingAccount, result, spec);

    // Jump forward to be unpredicted by the branch predictor.
    Stop:
        return EvmExceptionType.Stop;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    }

    /// <summary>
    /// Implements the PREVRANDAO opcode.
    /// Pushes the previous random value (post-merge) or block difficulty (pre-merge) onto the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPrevRandao(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Charge the base gas cost for this opcode.
        gasAvailable -= GasCostOf.Base;
        BlockHeader header = vm.EvmState.Env.TxExecutionContext.BlockExecutionContext.Header;

        // Use the random value if post-merge; otherwise, use block difficulty.
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

    /// <summary>
    /// Handles invalid opcodes by deducting a high gas cost and returning a BadInstruction error.
    /// </summary>
    public static EvmExceptionType InstructionInvalid(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.High;
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Default handler for undefined opcodes, always returning a BadInstruction error.
    /// </summary>
    public static EvmExceptionType InstructionBadInstruction(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        => EvmExceptionType.BadInstruction;

    /// <summary>
    /// Implements the EXP opcode to perform exponentiation.
    /// The operation deducts gas based on the size of the exponent and computes the result.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExp(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Charge the fixed gas cost for exponentiation.
        gasAvailable -= GasCostOf.Exp;

        // Pop the base value from the stack.
        if (!stack.PopUInt256(out UInt256 a))
            goto StackUnderflow;

        // Pop the exponent as a 256-bit word.
        Span<byte> bytes = stack.PopWord256();

        // Determine the effective byte-length of the exponent.
        int leadingZeros = bytes.LeadingZerosCount();
        if (leadingZeros == 32)
        {
            // Exponent is zero, so the result is 1.
            stack.PushOne();
        }
        else
        {
            int expSize = 32 - leadingZeros;
            // Deduct gas proportional to the number of 32-byte words needed to represent the exponent.
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
                // Perform exponentiation and push the 256-bit result onto the stack.
                UInt256.Exp(a, new UInt256(bytes, true), out UInt256 result);
                stack.PushUInt256(in result);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements the BYTE opcode.
    /// Extracts a byte from a 256-bit word at the position specified by the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionByte(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.VeryLow;

        // Pop the byte position and the 256-bit word.
        if (!stack.PopUInt256(out UInt256 a))
            goto StackUnderflow;
        Span<byte> bytes = stack.PopWord256();

        // If the position is out-of-range, push zero.
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
                // Push the extracted byte.
                stack.PushByte(bytes[adjustedPosition]);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements the SIGNEXTEND opcode.
    /// Performs sign extension on a 256-bit integer in-place based on a specified byte index.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSignExtend(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Low;

        // Pop the index to determine which byte to use for sign extension.
        if (!stack.PopUInt256(out UInt256 a))
            goto StackUnderflow;
        if (a >= BigInt32)
        {
            // If the index is out-of-range, no extension is needed.
            if (!stack.EnsureDepth(1))
                goto StackUnderflow;
            return EvmExceptionType.None;
        }

        int position = 31 - (int)a;

        // Peek at the 256-bit word without removing it.
        Span<byte> bytes = stack.PeekWord256();
        sbyte sign = (sbyte)bytes[position];

        // Extend the sign by replacing higher-order bytes.
        if (sign >= 0)
        {
            // Fill with zero bytes.
            BytesZero32.AsSpan(0, position).CopyTo(bytes[..position]);
        }
        else
        {
            // Fill with 0xFF bytes.
            BytesMax32.AsSpan(0, position).CopyTo(bytes[..position]);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Computes the Keccak-256 hash of a specified memory region.
    /// Pops a memory offset and length from the stack, charges gas based on the data size,
    /// and pushes the resulting 256-bit hash onto the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionKeccak256(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Ensure two 256-bit words are available (memory offset and length).
        if (!stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b))
            goto StackUnderflow;

        // Deduct gas: base cost plus additional cost per 32-byte word.
        gasAvailable -= GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in b, out bool outOfGas);
        if (outOfGas)
            goto OutOfGas;

        EvmState vmState = vm.EvmState;
        // Charge gas for any required memory expansion.
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, b))
            goto OutOfGas;

        // Load the target memory region.
        Span<byte> bytes = vmState.Memory.LoadSpan(in a, b);
        // Compute the Keccak-256 hash.
        KeccakCache.ComputeTo(bytes, out ValueHash256 keccak);
        // Push the 256-bit hash result onto the stack.
        stack.Push32Bytes(in Unsafe.As<ValueHash256, Vector256<byte>>(ref keccak));

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements the CALLDATALOAD opcode.
    /// Loads 32 bytes of call data starting from a position specified on the stack,
    /// zero-padding if necessary.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCallDataLoad(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.VeryLow;

        // Pop the offset from which to load call data.
        if (!stack.PopUInt256(out UInt256 result))
            goto StackUnderflow;
        // Load 32 bytes from input data, applying zero padding as needed.
        stack.PushBytes(vm.EvmState.Env.InputData.SliceWithZeroPadding(result, 32));

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Calculates and deducts the gas cost for accessing a specific memory region.
    /// </summary>
    /// <param name="vmState">The current EVM state.</param>
    /// <param name="gasAvailable">The remaining gas available.</param>
    /// <param name="position">The starting position in memory.</param>
    /// <param name="length">The length of the memory region.</param>
    /// <returns><c>true</c> if sufficient gas was available and deducted; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        // Calculate additional gas cost for any memory expansion.
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

    /// <summary>
    /// Deducts a specified gas cost from the available gas.
    /// </summary>
    /// <param name="gasCost">The gas cost to deduct.</param>
    /// <param name="gasAvailable">The remaining gas available.</param>
    /// <returns><c>true</c> if there was sufficient gas; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost)
        {
            return false;
        }

        gasAvailable -= gasCost;
        return true;
    }

    /// <summary>
    /// Refunds gas by adding the specified amount back to the available gas.
    /// </summary>
    /// <param name="refund">The gas amount to refund.</param>
    /// <param name="gasAvailable">The current gas available.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(long refund, ref long gasAvailable)
    {
        gasAvailable += refund;
    }
}
