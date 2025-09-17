// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

[assembly: InternalsVisibleTo("Nethermind.Evm.Precompiles")]
namespace Nethermind.Evm;
using unsafe OpCode = delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType>;

public static unsafe partial class EvmInstructions
{
    /// <summary>
    /// Generates the opcode lookup table for the Ethereum Virtual Machine.
    /// Each of the 256 entries in the returned array corresponds to an EVM instruction,
    /// with unassigned opcodes defaulting to a bad instruction handler.
    /// </summary>
    /// <typeparam name="TTracingInst">A struct implementing IFlag used for tracing purposes.</typeparam>
    /// <param name="spec">The release specification containing enabled features and opcode flags.</param>
    /// <returns>An array of function pointers (opcode handlers) indexed by opcode value.</returns>
    public static OpCode[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
    {
        // Allocate lookup table for all possible opcodes.
        OpCode[] lookup = new OpCode[byte.MaxValue + 1];

        for (int i = 0; i < lookup.Length; i++)
        {
            lookup[i] = &InstructionBadInstruction;
        }

        // Set basic control and arithmetic opcodes.
        lookup[(int)Instruction.STOP] = &InstructionStop;
        lookup[(int)Instruction.ADD] = &InstructionMath2Param<OpAdd, TTracingInst>;
        lookup[(int)Instruction.MUL] = &InstructionMath2Param<OpMul, TTracingInst>;
        lookup[(int)Instruction.SUB] = &InstructionMath2Param<OpSub, TTracingInst>;
        lookup[(int)Instruction.DIV] = &InstructionMath2Param<OpDiv, TTracingInst>;
        lookup[(int)Instruction.SDIV] = &InstructionMath2Param<OpSDiv, TTracingInst>;
        lookup[(int)Instruction.MOD] = &InstructionMath2Param<OpMod, TTracingInst>;
        lookup[(int)Instruction.SMOD] = &InstructionMath2Param<OpSMod, TTracingInst>;
        lookup[(int)Instruction.ADDMOD] = &InstructionMath3Param<OpAddMod, TTracingInst>;
        lookup[(int)Instruction.MULMOD] = &InstructionMath3Param<OpMulMod, TTracingInst>;
        lookup[(int)Instruction.EXP] = &InstructionExp<TTracingInst>;
        lookup[(int)Instruction.SIGNEXTEND] = &InstructionSignExtend<TTracingInst>;

        // Comparison and bitwise opcodes.
        lookup[(int)Instruction.LT] = &InstructionMath2Param<OpLt, TTracingInst>;
        lookup[(int)Instruction.GT] = &InstructionMath2Param<OpGt, TTracingInst>;
        lookup[(int)Instruction.SLT] = &InstructionMath2Param<OpSLt, TTracingInst>;
        lookup[(int)Instruction.SGT] = &InstructionMath2Param<OpSGt, TTracingInst>;
        lookup[(int)Instruction.EQ] = &InstructionBitwise<OpBitwiseEq>;
        lookup[(int)Instruction.ISZERO] = &InstructionMath1Param<OpIsZero>;
        lookup[(int)Instruction.AND] = &InstructionBitwise<OpBitwiseAnd>;
        lookup[(int)Instruction.OR] = &InstructionBitwise<OpBitwiseOr>;
        lookup[(int)Instruction.XOR] = &InstructionBitwise<OpBitwiseXor>;
        lookup[(int)Instruction.NOT] = &InstructionMath1Param<OpNot>;
        lookup[(int)Instruction.BYTE] = &InstructionByte<TTracingInst>;

        // Conditional: enable shift opcodes if the spec allows.
        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = &InstructionShift<OpShl, TTracingInst>;
            lookup[(int)Instruction.SHR] = &InstructionShift<OpShr, TTracingInst>;
            lookup[(int)Instruction.SAR] = &InstructionSar<TTracingInst>;
        }

        if (spec.CLZEnabled)
        {
            lookup[(int)Instruction.CLZ] = &InstructionMath1Param<OpCLZ>;
        }

        // Cryptographic hash opcode.
        lookup[(int)Instruction.KECCAK256] = &InstructionKeccak256<TTracingInst>;

        // Environment opcodes.
        lookup[(int)Instruction.ADDRESS] = &InstructionEnvAddress<OpAddress, TTracingInst>;
        lookup[(int)Instruction.BALANCE] = &InstructionBalance<TTracingInst>;
        lookup[(int)Instruction.ORIGIN] = &InstructionEnv32Bytes<OpOrigin, TTracingInst>;
        lookup[(int)Instruction.CALLER] = &InstructionEnvAddress<OpCaller, TTracingInst>;
        lookup[(int)Instruction.CALLVALUE] = &InstructionEnvUInt256<OpCallValue, TTracingInst>;
        lookup[(int)Instruction.CALLDATALOAD] = &InstructionCallDataLoad<TTracingInst>;
        lookup[(int)Instruction.CALLDATASIZE] = &InstructionEnvUInt32<OpCallDataSize, TTracingInst>;
        lookup[(int)Instruction.CALLDATACOPY] = &InstructionCodeCopy<OpCallDataCopy, TTracingInst>;
        lookup[(int)Instruction.CODESIZE] = &InstructionEnvUInt32<OpCodeSize, TTracingInst>;
        lookup[(int)Instruction.CODECOPY] = &InstructionCodeCopy<OpCodeCopy, TTracingInst>;
        lookup[(int)Instruction.GASPRICE] = &InstructionBlkUInt256<OpGasPrice, TTracingInst>;

        lookup[(int)Instruction.EXTCODESIZE] = &InstructionExtCodeSize<TTracingInst>;
        lookup[(int)Instruction.EXTCODECOPY] = &InstructionExtCodeCopy<TTracingInst>;

        // Return data opcodes (if enabled).
        if (spec.ReturnDataOpcodesEnabled)
        {
            lookup[(int)Instruction.RETURNDATASIZE] = &InstructionReturnDataSize<TTracingInst>;
            lookup[(int)Instruction.RETURNDATACOPY] = &InstructionReturnDataCopy<TTracingInst>;
        }

        // Extended code hash opcode handling.
        if (spec.ExtCodeHashOpcodeEnabled)
        {
            lookup[(int)Instruction.EXTCODEHASH] = spec.IsEofEnabled ?
                &InstructionExtCodeHashEof<TTracingInst> :
                &InstructionExtCodeHash<TTracingInst>;
        }

        lookup[(int)Instruction.BLOCKHASH] = &InstructionBlockHash<TTracingInst>;

        // More environment opcodes.
        lookup[(int)Instruction.COINBASE] = &InstructionBlkAddress<OpCoinbase, TTracingInst>;
        lookup[(int)Instruction.TIMESTAMP] = &InstructionBlkUInt64<OpTimestamp, TTracingInst>;
        lookup[(int)Instruction.NUMBER] = &InstructionBlkUInt64<OpNumber, TTracingInst>;
        lookup[(int)Instruction.PREVRANDAO] = &InstructionPrevRandao<TTracingInst>;
        lookup[(int)Instruction.GASLIMIT] = &InstructionBlkUInt64<OpGasLimit, TTracingInst>;
        if (spec.ChainIdOpcodeEnabled)
        {
            lookup[(int)Instruction.CHAINID] = &InstructionEnv32Bytes<OpChainId, TTracingInst>;
        }
        if (spec.SelfBalanceOpcodeEnabled)
        {
            lookup[(int)Instruction.SELFBALANCE] = &InstructionSelfBalance<TTracingInst>;
        }
        if (spec.BaseFeeEnabled)
        {
            lookup[(int)Instruction.BASEFEE] = &InstructionBlkUInt256<OpBaseFee, TTracingInst>;
        }
        if (spec.IsEip4844Enabled)
        {
            lookup[(int)Instruction.BLOBHASH] = &InstructionBlobHash<TTracingInst>;
        }
        if (spec.BlobBaseFeeEnabled)
        {
            lookup[(int)Instruction.BLOBBASEFEE] = &InstructionBlobBaseFee<TTracingInst>;
        }

        // Gap: opcodes 0x4b to 0x4f are unassigned.

        // Memory and storage instructions.
        lookup[(int)Instruction.POP] = &InstructionPop;
        lookup[(int)Instruction.MLOAD] = &InstructionMLoad<TTracingInst>;
        lookup[(int)Instruction.MSTORE] = &InstructionMStore<TTracingInst>;
        lookup[(int)Instruction.MSTORE8] = &InstructionMStore8<TTracingInst>;
        lookup[(int)Instruction.SLOAD] = &InstructionSLoad<TTracingInst>;
        lookup[(int)Instruction.SSTORE] = spec.UseNetGasMetering ?
            (spec.UseNetGasMeteringWithAStipendFix ?
                &InstructionSStoreMetered<TTracingInst, OnFlag> :
                &InstructionSStoreMetered<TTracingInst, OffFlag>
            ) :
            &InstructionSStoreUnmetered<TTracingInst>;

        // Jump instructions.
        lookup[(int)Instruction.JUMP] = &InstructionJump;
        lookup[(int)Instruction.JUMPI] = &InstructionJumpIf;
        lookup[(int)Instruction.PC] = &InstructionProgramCounter<TTracingInst>;
        lookup[(int)Instruction.MSIZE] = &InstructionEnvUInt64<OpMSize, TTracingInst>;
        lookup[(int)Instruction.GAS] = &InstructionGas<TTracingInst>;
        lookup[(int)Instruction.JUMPDEST] = &InstructionJumpDest;

        // Transient storage opcodes.
        if (spec.TransientStorageEnabled)
        {
            lookup[(int)Instruction.TLOAD] = &InstructionTLoad<TTracingInst>;
            lookup[(int)Instruction.TSTORE] = &InstructionTStore;
        }
        if (spec.MCopyIncluded)
        {
            lookup[(int)Instruction.MCOPY] = &InstructionMCopy<TTracingInst>;
        }

        // Optional PUSH0 instruction.
        if (spec.IncludePush0Instruction)
        {
            lookup[(int)Instruction.PUSH0] = &InstructionPush0<TTracingInst>;
        }

        // PUSH opcodes (PUSH1 to PUSH32).
        lookup[(int)Instruction.PUSH1] = &InstructionPush<Op1, TTracingInst>;
        lookup[(int)Instruction.PUSH2] = &InstructionPush2<TTracingInst>;
        lookup[(int)Instruction.PUSH3] = &InstructionPush<Op3, TTracingInst>;
        lookup[(int)Instruction.PUSH4] = &InstructionPush<Op4, TTracingInst>;
        lookup[(int)Instruction.PUSH5] = &InstructionPush<Op5, TTracingInst>;
        lookup[(int)Instruction.PUSH6] = &InstructionPush<Op6, TTracingInst>;
        lookup[(int)Instruction.PUSH7] = &InstructionPush<Op7, TTracingInst>;
        lookup[(int)Instruction.PUSH8] = &InstructionPush<Op8, TTracingInst>;
        lookup[(int)Instruction.PUSH9] = &InstructionPush<Op9, TTracingInst>;
        lookup[(int)Instruction.PUSH10] = &InstructionPush<Op10, TTracingInst>;
        lookup[(int)Instruction.PUSH11] = &InstructionPush<Op11, TTracingInst>;
        lookup[(int)Instruction.PUSH12] = &InstructionPush<Op12, TTracingInst>;
        lookup[(int)Instruction.PUSH13] = &InstructionPush<Op13, TTracingInst>;
        lookup[(int)Instruction.PUSH14] = &InstructionPush<Op14, TTracingInst>;
        lookup[(int)Instruction.PUSH15] = &InstructionPush<Op15, TTracingInst>;
        lookup[(int)Instruction.PUSH16] = &InstructionPush<Op16, TTracingInst>;
        lookup[(int)Instruction.PUSH17] = &InstructionPush<Op17, TTracingInst>;
        lookup[(int)Instruction.PUSH18] = &InstructionPush<Op18, TTracingInst>;
        lookup[(int)Instruction.PUSH19] = &InstructionPush<Op19, TTracingInst>;
        lookup[(int)Instruction.PUSH20] = &InstructionPush<Op20, TTracingInst>;
        lookup[(int)Instruction.PUSH21] = &InstructionPush<Op21, TTracingInst>;
        lookup[(int)Instruction.PUSH22] = &InstructionPush<Op22, TTracingInst>;
        lookup[(int)Instruction.PUSH23] = &InstructionPush<Op23, TTracingInst>;
        lookup[(int)Instruction.PUSH24] = &InstructionPush<Op24, TTracingInst>;
        lookup[(int)Instruction.PUSH25] = &InstructionPush<Op25, TTracingInst>;
        lookup[(int)Instruction.PUSH26] = &InstructionPush<Op26, TTracingInst>;
        lookup[(int)Instruction.PUSH27] = &InstructionPush<Op27, TTracingInst>;
        lookup[(int)Instruction.PUSH28] = &InstructionPush<Op28, TTracingInst>;
        lookup[(int)Instruction.PUSH29] = &InstructionPush<Op29, TTracingInst>;
        lookup[(int)Instruction.PUSH30] = &InstructionPush<Op30, TTracingInst>;
        lookup[(int)Instruction.PUSH31] = &InstructionPush<Op31, TTracingInst>;
        lookup[(int)Instruction.PUSH32] = &InstructionPush<Op32, TTracingInst>;

        // DUP opcodes (DUP1 to DUP16).
        lookup[(int)Instruction.DUP1] = &InstructionDup<Op1, TTracingInst>;
        lookup[(int)Instruction.DUP2] = &InstructionDup<Op2, TTracingInst>;
        lookup[(int)Instruction.DUP3] = &InstructionDup<Op3, TTracingInst>;
        lookup[(int)Instruction.DUP4] = &InstructionDup<Op4, TTracingInst>;
        lookup[(int)Instruction.DUP5] = &InstructionDup<Op5, TTracingInst>;
        lookup[(int)Instruction.DUP6] = &InstructionDup<Op6, TTracingInst>;
        lookup[(int)Instruction.DUP7] = &InstructionDup<Op7, TTracingInst>;
        lookup[(int)Instruction.DUP8] = &InstructionDup<Op8, TTracingInst>;
        lookup[(int)Instruction.DUP9] = &InstructionDup<Op9, TTracingInst>;
        lookup[(int)Instruction.DUP10] = &InstructionDup<Op10, TTracingInst>;
        lookup[(int)Instruction.DUP11] = &InstructionDup<Op11, TTracingInst>;
        lookup[(int)Instruction.DUP12] = &InstructionDup<Op12, TTracingInst>;
        lookup[(int)Instruction.DUP13] = &InstructionDup<Op13, TTracingInst>;
        lookup[(int)Instruction.DUP14] = &InstructionDup<Op14, TTracingInst>;
        lookup[(int)Instruction.DUP15] = &InstructionDup<Op15, TTracingInst>;
        lookup[(int)Instruction.DUP16] = &InstructionDup<Op16, TTracingInst>;

        // SWAP opcodes (SWAP1 to SWAP16).
        lookup[(int)Instruction.SWAP1] = &InstructionSwap<Op1, TTracingInst>;
        lookup[(int)Instruction.SWAP2] = &InstructionSwap<Op2, TTracingInst>;
        lookup[(int)Instruction.SWAP3] = &InstructionSwap<Op3, TTracingInst>;
        lookup[(int)Instruction.SWAP4] = &InstructionSwap<Op4, TTracingInst>;
        lookup[(int)Instruction.SWAP5] = &InstructionSwap<Op5, TTracingInst>;
        lookup[(int)Instruction.SWAP6] = &InstructionSwap<Op6, TTracingInst>;
        lookup[(int)Instruction.SWAP7] = &InstructionSwap<Op7, TTracingInst>;
        lookup[(int)Instruction.SWAP8] = &InstructionSwap<Op8, TTracingInst>;
        lookup[(int)Instruction.SWAP9] = &InstructionSwap<Op9, TTracingInst>;
        lookup[(int)Instruction.SWAP10] = &InstructionSwap<Op10, TTracingInst>;
        lookup[(int)Instruction.SWAP11] = &InstructionSwap<Op11, TTracingInst>;
        lookup[(int)Instruction.SWAP12] = &InstructionSwap<Op12, TTracingInst>;
        lookup[(int)Instruction.SWAP13] = &InstructionSwap<Op13, TTracingInst>;
        lookup[(int)Instruction.SWAP14] = &InstructionSwap<Op14, TTracingInst>;
        lookup[(int)Instruction.SWAP15] = &InstructionSwap<Op15, TTracingInst>;
        lookup[(int)Instruction.SWAP16] = &InstructionSwap<Op16, TTracingInst>;

        // LOG opcodes.
        lookup[(int)Instruction.LOG0] = &InstructionLog<Op0>;
        lookup[(int)Instruction.LOG1] = &InstructionLog<Op1>;
        lookup[(int)Instruction.LOG2] = &InstructionLog<Op2>;
        lookup[(int)Instruction.LOG3] = &InstructionLog<Op3>;
        lookup[(int)Instruction.LOG4] = &InstructionLog<Op4>;

        // Extended opcodes for EO (EoF) mode.
        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.DATALOAD] = &InstructionDataLoad<TTracingInst>;
            lookup[(int)Instruction.DATALOADN] = &InstructionDataLoadN<TTracingInst>;
            lookup[(int)Instruction.DATASIZE] = &InstructionDataSize<TTracingInst>;
            lookup[(int)Instruction.DATACOPY] = &InstructionDataCopy<TTracingInst>;
            lookup[(int)Instruction.RJUMP] = &InstructionRelativeJump;
            lookup[(int)Instruction.RJUMPI] = &InstructionRelativeJumpIf;
            lookup[(int)Instruction.RJUMPV] = &InstructionJumpTable;
            lookup[(int)Instruction.CALLF] = &InstructionCallFunction;
            lookup[(int)Instruction.RETF] = &InstructionReturnFunction;
            lookup[(int)Instruction.JUMPF] = &InstructionJumpFunction;
            lookup[(int)Instruction.DUPN] = &InstructionDupN<TTracingInst>;
            lookup[(int)Instruction.SWAPN] = &InstructionSwapN<TTracingInst>;
            lookup[(int)Instruction.EXCHANGE] = &InstructionExchange<TTracingInst>;
            lookup[(int)Instruction.EOFCREATE] = &InstructionEofCreate<TTracingInst>;
            lookup[(int)Instruction.RETURNCODE] = &InstructionReturnCode;
        }

        // Contract creation and call opcodes.
        lookup[(int)Instruction.CREATE] = &InstructionCreate<OpCreate, TTracingInst>;
        lookup[(int)Instruction.CALL] = &InstructionCall<OpCall, TTracingInst>;
        lookup[(int)Instruction.CALLCODE] = &InstructionCall<OpCallCode, TTracingInst>;
        lookup[(int)Instruction.RETURN] = &InstructionReturn;
        if (spec.DelegateCallEnabled)
        {
            lookup[(int)Instruction.DELEGATECALL] = &InstructionCall<OpDelegateCall, TTracingInst>;
        }
        if (spec.Create2OpcodeEnabled)
        {
            lookup[(int)Instruction.CREATE2] = &InstructionCreate<OpCreate2, TTracingInst>;
        }

        lookup[(int)Instruction.RETURNDATALOAD] = &InstructionReturnDataLoad<TTracingInst>;
        if (spec.StaticCallEnabled)
        {
            lookup[(int)Instruction.STATICCALL] = &InstructionCall<OpStaticCall, TTracingInst>;
        }

        // Extended call opcodes in EO mode.
        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.EXTCALL] = &InstructionEofCall<OpEofCall, TTracingInst>;
            if (spec.DelegateCallEnabled)
            {
                lookup[(int)Instruction.EXTDELEGATECALL] = &InstructionEofCall<OpEofDelegateCall, TTracingInst>;
            }
            if (spec.StaticCallEnabled)
            {
                lookup[(int)Instruction.EXTSTATICCALL] = &InstructionEofCall<OpEofStaticCall, TTracingInst>;
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
}
