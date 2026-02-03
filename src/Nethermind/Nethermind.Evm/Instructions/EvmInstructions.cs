// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

[assembly: InternalsVisibleTo("Nethermind.Evm.Precompiles")]
namespace Nethermind.Evm;

internal static unsafe partial class EvmInstructions
{
    /// <summary>
    /// Generates the opcode lookup table for the Ethereum Virtual Machine.
    /// Each of the 256 entries in the returned array corresponds to an EVM instruction,
    /// with unassigned opcodes defaulting to a bad instruction handler.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type used for gas accounting.</typeparam>
    /// <typeparam name="TTracingInst">A struct implementing IFlag used for tracing purposes.</typeparam>
    /// <param name="spec">The release specification containing enabled features and opcode flags.</param>
    /// <returns>An array of function pointers (opcode handlers) indexed by opcode value.</returns>
    public static delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] GenerateOpCodes<TGasPolicy, TTracingInst>(IReleaseSpec spec)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Allocate lookup table for all possible opcodes.
        var lookup = new delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[byte.MaxValue + 1];

        for (int i = 0; i < lookup.Length; i++)
        {
            lookup[i] = &InstructionBadInstruction;
        }

        // Set basic control and arithmetic opcodes.
        lookup[(int)Instruction.STOP] = &InstructionStop;
        lookup[(int)Instruction.ADD] = &InstructionMath2Param<TGasPolicy, OpAdd, TTracingInst>;
        lookup[(int)Instruction.MUL] = &InstructionMath2Param<TGasPolicy, OpMul, TTracingInst>;
        lookup[(int)Instruction.SUB] = &InstructionMath2Param<TGasPolicy, OpSub, TTracingInst>;
        lookup[(int)Instruction.DIV] = &InstructionMath2Param<TGasPolicy, OpDiv, TTracingInst>;
        lookup[(int)Instruction.SDIV] = &InstructionMath2Param<TGasPolicy, OpSDiv, TTracingInst>;
        lookup[(int)Instruction.MOD] = &InstructionMath2Param<TGasPolicy, OpMod, TTracingInst>;
        lookup[(int)Instruction.SMOD] = &InstructionMath2Param<TGasPolicy, OpSMod, TTracingInst>;
        lookup[(int)Instruction.ADDMOD] = &InstructionMath3Param<TGasPolicy, OpAddMod, TTracingInst>;
        lookup[(int)Instruction.MULMOD] = &InstructionMath3Param<TGasPolicy, OpMulMod, TTracingInst>;
        lookup[(int)Instruction.EXP] = &InstructionExp<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.SIGNEXTEND] = &InstructionSignExtend<TGasPolicy>;

        // Comparison and bitwise opcodes.
        lookup[(int)Instruction.LT] = &InstructionMath2Param<TGasPolicy, OpLt, TTracingInst>;
        lookup[(int)Instruction.GT] = &InstructionMath2Param<TGasPolicy, OpGt, TTracingInst>;
        lookup[(int)Instruction.SLT] = &InstructionMath2Param<TGasPolicy, OpSLt, TTracingInst>;
        lookup[(int)Instruction.SGT] = &InstructionMath2Param<TGasPolicy, OpSGt, TTracingInst>;
        lookup[(int)Instruction.EQ] = &InstructionBitwise<TGasPolicy, OpBitwiseEq>;
        lookup[(int)Instruction.ISZERO] = &InstructionMath1Param<TGasPolicy, OpIsZero>;
        lookup[(int)Instruction.AND] = &InstructionBitwise<TGasPolicy, OpBitwiseAnd>;
        lookup[(int)Instruction.OR] = &InstructionBitwise<TGasPolicy, OpBitwiseOr>;
        lookup[(int)Instruction.XOR] = &InstructionBitwise<TGasPolicy, OpBitwiseXor>;
        lookup[(int)Instruction.NOT] = &InstructionMath1Param<TGasPolicy, OpNot>;
        lookup[(int)Instruction.BYTE] = &InstructionByte<TGasPolicy, TTracingInst>;

        // Conditional: enable shift opcodes if the spec allows.
        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = &InstructionShift<TGasPolicy, OpShl, TTracingInst>;
            lookup[(int)Instruction.SHR] = &InstructionShift<TGasPolicy, OpShr, TTracingInst>;
            lookup[(int)Instruction.SAR] = &InstructionSar<TGasPolicy, TTracingInst>;
        }

        if (spec.CLZEnabled)
        {
            lookup[(int)Instruction.CLZ] = &InstructionMath1Param<TGasPolicy, OpCLZ>;
        }

        // Cryptographic hash opcode.
        lookup[(int)Instruction.KECCAK256] = &InstructionKeccak256<TGasPolicy, TTracingInst>;

        // Environment opcodes.
        lookup[(int)Instruction.ADDRESS] = &InstructionEnvAddress<TGasPolicy, OpAddress<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.BALANCE] = &InstructionBalance<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.ORIGIN] = &InstructionEnv32Bytes<TGasPolicy, OpOrigin<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.CALLER] = &InstructionEnvAddress<TGasPolicy, OpCaller<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.CALLVALUE] = &InstructionEnvUInt256<TGasPolicy, OpCallValue<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.CALLDATALOAD] = &InstructionCallDataLoad<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.CALLDATASIZE] = &InstructionEnvUInt32<TGasPolicy, OpCallDataSize<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.CALLDATACOPY] =
            &InstructionCodeCopy<TGasPolicy, OpCallDataCopy<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.CODESIZE] = &InstructionEnvUInt32<TGasPolicy, OpCodeSize<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.CODECOPY] = &InstructionCodeCopy<TGasPolicy, OpCodeCopy<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.GASPRICE] = &InstructionBlkUInt256<TGasPolicy, OpGasPrice<TGasPolicy>, TTracingInst>;

        lookup[(int)Instruction.EXTCODESIZE] = &InstructionExtCodeSize<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.EXTCODECOPY] = &InstructionExtCodeCopy<TGasPolicy, TTracingInst>;

        // Return data opcodes (if enabled).
        if (spec.ReturnDataOpcodesEnabled)
        {
            lookup[(int)Instruction.RETURNDATASIZE] = &InstructionReturnDataSize<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.RETURNDATACOPY] = &InstructionReturnDataCopy<TGasPolicy, TTracingInst>;
        }

        // Extended code hash opcode handling.
        if (spec.ExtCodeHashOpcodeEnabled)
        {
            lookup[(int)Instruction.EXTCODEHASH] = spec.IsEofEnabled ?
                &InstructionExtCodeHashEof<TGasPolicy, TTracingInst> :
                &InstructionExtCodeHash<TGasPolicy, TTracingInst>;
        }

        lookup[(int)Instruction.BLOCKHASH] = &InstructionBlockHash<TGasPolicy, TTracingInst>;

        // More environment opcodes.
        lookup[(int)Instruction.COINBASE] = &InstructionBlkAddress<TGasPolicy, OpCoinbase<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.TIMESTAMP] = &InstructionBlkUInt64<TGasPolicy, OpTimestamp<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.NUMBER] = &InstructionBlkUInt64<TGasPolicy, OpNumber<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.PREVRANDAO] = &InstructionPrevRandao<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.GASLIMIT] = &InstructionBlkUInt64<TGasPolicy, OpGasLimit<TGasPolicy>, TTracingInst>;
        if (spec.ChainIdOpcodeEnabled)
        {
            lookup[(int)Instruction.CHAINID] = &InstructionEnv32Bytes<TGasPolicy, OpChainId<TGasPolicy>, TTracingInst>;
        }
        if (spec.SelfBalanceOpcodeEnabled)
        {
            lookup[(int)Instruction.SELFBALANCE] = &InstructionSelfBalance<TGasPolicy, TTracingInst>;
        }
        if (spec.BaseFeeEnabled)
        {
            lookup[(int)Instruction.BASEFEE] = &InstructionBlkUInt256<TGasPolicy, OpBaseFee<TGasPolicy>, TTracingInst>;
        }
        if (spec.IsEip4844Enabled)
        {
            lookup[(int)Instruction.BLOBHASH] = &InstructionBlobHash<TGasPolicy, TTracingInst>;
        }
        if (spec.BlobBaseFeeEnabled)
        {
            lookup[(int)Instruction.BLOBBASEFEE] = &InstructionBlobBaseFee<TGasPolicy, TTracingInst>;
        }
        if (spec.IsEip7843Enabled)
        {
            lookup[(int)Instruction.SLOTNUM] = &InstructionSlotNum<TGasPolicy, TTracingInst>;
        }

        // Gap: opcodes 0x4c to 0x4f are unassigned.

        // Memory and storage instructions.
        lookup[(int)Instruction.POP] = &InstructionPop;
        lookup[(int)Instruction.MLOAD] = &InstructionMLoad<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.MSTORE] = &InstructionMStore<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.MSTORE8] = &InstructionMStore8<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.SLOAD] = &InstructionSLoad<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.SSTORE] = spec.UseNetGasMetering ?
            (spec.UseNetGasMeteringWithAStipendFix ?
                &InstructionSStoreMetered<TGasPolicy, TTracingInst, OnFlag> :
                &InstructionSStoreMetered<TGasPolicy, TTracingInst, OffFlag>
            ) :
            &InstructionSStoreUnmetered<TGasPolicy, TTracingInst>;

        // Jump instructions.
        lookup[(int)Instruction.JUMP] = &InstructionJump;
        lookup[(int)Instruction.JUMPI] = &InstructionJumpIf;
        lookup[(int)Instruction.PC] = &InstructionProgramCounter<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.MSIZE] = &InstructionEnvUInt64<TGasPolicy, OpMSize<TGasPolicy>, TTracingInst>;
        lookup[(int)Instruction.GAS] = &InstructionGas<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.JUMPDEST] = &InstructionJumpDest;

        // Transient storage opcodes.
        if (spec.TransientStorageEnabled)
        {
            lookup[(int)Instruction.TLOAD] = &InstructionTLoad<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.TSTORE] = &InstructionTStore;
        }
        if (spec.MCopyIncluded)
        {
            lookup[(int)Instruction.MCOPY] = &InstructionMCopy<TGasPolicy, TTracingInst>;
        }

        // Optional PUSH0 instruction.
        if (spec.IncludePush0Instruction)
        {
            lookup[(int)Instruction.PUSH0] = &InstructionPush0<TGasPolicy, TTracingInst>;
        }

        // PUSH opcodes (PUSH1 to PUSH32).
        lookup[(int)Instruction.PUSH1] = &InstructionPush<TGasPolicy, Op1, TTracingInst>;
        lookup[(int)Instruction.PUSH2] = &InstructionPush2<TGasPolicy, TTracingInst>;
        lookup[(int)Instruction.PUSH3] = &InstructionPush<TGasPolicy, Op3, TTracingInst>;
        lookup[(int)Instruction.PUSH4] = &InstructionPush<TGasPolicy, Op4, TTracingInst>;
        lookup[(int)Instruction.PUSH5] = &InstructionPush<TGasPolicy, Op5, TTracingInst>;
        lookup[(int)Instruction.PUSH6] = &InstructionPush<TGasPolicy, Op6, TTracingInst>;
        lookup[(int)Instruction.PUSH7] = &InstructionPush<TGasPolicy, Op7, TTracingInst>;
        lookup[(int)Instruction.PUSH8] = &InstructionPush<TGasPolicy, Op8, TTracingInst>;
        lookup[(int)Instruction.PUSH9] = &InstructionPush<TGasPolicy, Op9, TTracingInst>;
        lookup[(int)Instruction.PUSH10] = &InstructionPush<TGasPolicy, Op10, TTracingInst>;
        lookup[(int)Instruction.PUSH11] = &InstructionPush<TGasPolicy, Op11, TTracingInst>;
        lookup[(int)Instruction.PUSH12] = &InstructionPush<TGasPolicy, Op12, TTracingInst>;
        lookup[(int)Instruction.PUSH13] = &InstructionPush<TGasPolicy, Op13, TTracingInst>;
        lookup[(int)Instruction.PUSH14] = &InstructionPush<TGasPolicy, Op14, TTracingInst>;
        lookup[(int)Instruction.PUSH15] = &InstructionPush<TGasPolicy, Op15, TTracingInst>;
        lookup[(int)Instruction.PUSH16] = &InstructionPush<TGasPolicy, Op16, TTracingInst>;
        lookup[(int)Instruction.PUSH17] = &InstructionPush<TGasPolicy, Op17, TTracingInst>;
        lookup[(int)Instruction.PUSH18] = &InstructionPush<TGasPolicy, Op18, TTracingInst>;
        lookup[(int)Instruction.PUSH19] = &InstructionPush<TGasPolicy, Op19, TTracingInst>;
        lookup[(int)Instruction.PUSH20] = &InstructionPush<TGasPolicy, Op20, TTracingInst>;
        lookup[(int)Instruction.PUSH21] = &InstructionPush<TGasPolicy, Op21, TTracingInst>;
        lookup[(int)Instruction.PUSH22] = &InstructionPush<TGasPolicy, Op22, TTracingInst>;
        lookup[(int)Instruction.PUSH23] = &InstructionPush<TGasPolicy, Op23, TTracingInst>;
        lookup[(int)Instruction.PUSH24] = &InstructionPush<TGasPolicy, Op24, TTracingInst>;
        lookup[(int)Instruction.PUSH25] = &InstructionPush<TGasPolicy, Op25, TTracingInst>;
        lookup[(int)Instruction.PUSH26] = &InstructionPush<TGasPolicy, Op26, TTracingInst>;
        lookup[(int)Instruction.PUSH27] = &InstructionPush<TGasPolicy, Op27, TTracingInst>;
        lookup[(int)Instruction.PUSH28] = &InstructionPush<TGasPolicy, Op28, TTracingInst>;
        lookup[(int)Instruction.PUSH29] = &InstructionPush<TGasPolicy, Op29, TTracingInst>;
        lookup[(int)Instruction.PUSH30] = &InstructionPush<TGasPolicy, Op30, TTracingInst>;
        lookup[(int)Instruction.PUSH31] = &InstructionPush<TGasPolicy, Op31, TTracingInst>;
        lookup[(int)Instruction.PUSH32] = &InstructionPush<TGasPolicy, Op32, TTracingInst>;

        // DUP opcodes (DUP1 to DUP16).
        lookup[(int)Instruction.DUP1] = &InstructionDup<TGasPolicy, Op1, TTracingInst>;
        lookup[(int)Instruction.DUP2] = &InstructionDup<TGasPolicy, Op2, TTracingInst>;
        lookup[(int)Instruction.DUP3] = &InstructionDup<TGasPolicy, Op3, TTracingInst>;
        lookup[(int)Instruction.DUP4] = &InstructionDup<TGasPolicy, Op4, TTracingInst>;
        lookup[(int)Instruction.DUP5] = &InstructionDup<TGasPolicy, Op5, TTracingInst>;
        lookup[(int)Instruction.DUP6] = &InstructionDup<TGasPolicy, Op6, TTracingInst>;
        lookup[(int)Instruction.DUP7] = &InstructionDup<TGasPolicy, Op7, TTracingInst>;
        lookup[(int)Instruction.DUP8] = &InstructionDup<TGasPolicy, Op8, TTracingInst>;
        lookup[(int)Instruction.DUP9] = &InstructionDup<TGasPolicy, Op9, TTracingInst>;
        lookup[(int)Instruction.DUP10] = &InstructionDup<TGasPolicy, Op10, TTracingInst>;
        lookup[(int)Instruction.DUP11] = &InstructionDup<TGasPolicy, Op11, TTracingInst>;
        lookup[(int)Instruction.DUP12] = &InstructionDup<TGasPolicy, Op12, TTracingInst>;
        lookup[(int)Instruction.DUP13] = &InstructionDup<TGasPolicy, Op13, TTracingInst>;
        lookup[(int)Instruction.DUP14] = &InstructionDup<TGasPolicy, Op14, TTracingInst>;
        lookup[(int)Instruction.DUP15] = &InstructionDup<TGasPolicy, Op15, TTracingInst>;
        lookup[(int)Instruction.DUP16] = &InstructionDup<TGasPolicy, Op16, TTracingInst>;

        // SWAP opcodes (SWAP1 to SWAP16).
        lookup[(int)Instruction.SWAP1] = &InstructionSwap<TGasPolicy, Op1, TTracingInst>;
        lookup[(int)Instruction.SWAP2] = &InstructionSwap<TGasPolicy, Op2, TTracingInst>;
        lookup[(int)Instruction.SWAP3] = &InstructionSwap<TGasPolicy, Op3, TTracingInst>;
        lookup[(int)Instruction.SWAP4] = &InstructionSwap<TGasPolicy, Op4, TTracingInst>;
        lookup[(int)Instruction.SWAP5] = &InstructionSwap<TGasPolicy, Op5, TTracingInst>;
        lookup[(int)Instruction.SWAP6] = &InstructionSwap<TGasPolicy, Op6, TTracingInst>;
        lookup[(int)Instruction.SWAP7] = &InstructionSwap<TGasPolicy, Op7, TTracingInst>;
        lookup[(int)Instruction.SWAP8] = &InstructionSwap<TGasPolicy, Op8, TTracingInst>;
        lookup[(int)Instruction.SWAP9] = &InstructionSwap<TGasPolicy, Op9, TTracingInst>;
        lookup[(int)Instruction.SWAP10] = &InstructionSwap<TGasPolicy, Op10, TTracingInst>;
        lookup[(int)Instruction.SWAP11] = &InstructionSwap<TGasPolicy, Op11, TTracingInst>;
        lookup[(int)Instruction.SWAP12] = &InstructionSwap<TGasPolicy, Op12, TTracingInst>;
        lookup[(int)Instruction.SWAP13] = &InstructionSwap<TGasPolicy, Op13, TTracingInst>;
        lookup[(int)Instruction.SWAP14] = &InstructionSwap<TGasPolicy, Op14, TTracingInst>;
        lookup[(int)Instruction.SWAP15] = &InstructionSwap<TGasPolicy, Op15, TTracingInst>;
        lookup[(int)Instruction.SWAP16] = &InstructionSwap<TGasPolicy, Op16, TTracingInst>;

        // LOG opcodes.
        lookup[(int)Instruction.LOG0] = &InstructionLog<TGasPolicy, Op0>;
        lookup[(int)Instruction.LOG1] = &InstructionLog<TGasPolicy, Op1>;
        lookup[(int)Instruction.LOG2] = &InstructionLog<TGasPolicy, Op2>;
        lookup[(int)Instruction.LOG3] = &InstructionLog<TGasPolicy, Op3>;
        lookup[(int)Instruction.LOG4] = &InstructionLog<TGasPolicy, Op4>;

        // EIP-8024: Backward-compatible stack operations for legacy code.
        // These are registered first and will be overridden by EOF handlers if EOF is enabled.
        if (spec.IsEip8024Enabled)
        {
            lookup[(int)Instruction.DUPN] = &InstructionDupN<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.SWAPN] = &InstructionSwapN<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.EXCHANGE] = &InstructionExchange<TGasPolicy, TTracingInst>;
        }

        // Extended opcodes for EO (EoF) mode.
        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.DATALOAD] = &InstructionDataLoad<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.DATALOADN] = &InstructionDataLoadN<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.DATASIZE] = &InstructionDataSize<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.DATACOPY] = &InstructionDataCopy<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.RJUMP] = &InstructionRelativeJump;
            lookup[(int)Instruction.RJUMPI] = &InstructionRelativeJumpIf;
            lookup[(int)Instruction.RJUMPV] = &InstructionJumpTable;
            lookup[(int)Instruction.CALLF] = &InstructionCallFunction;
            lookup[(int)Instruction.RETF] = &InstructionReturnFunction;
            lookup[(int)Instruction.JUMPF] = &InstructionJumpFunction;
            lookup[(int)Instruction.DUPN] = &InstructionEofDupN<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.SWAPN] = &InstructionEofSwapN<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.EXCHANGE] = &InstructionEofExchange<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.EOFCREATE] = &InstructionEofCreate<TGasPolicy, TTracingInst>;
            lookup[(int)Instruction.RETURNCODE] = &InstructionReturnCode;
        }

        // Contract creation and call opcodes.
        lookup[(int)Instruction.CREATE] = &InstructionCreate<TGasPolicy, OpCreate, TTracingInst>;
        lookup[(int)Instruction.CALL] = &InstructionCall<TGasPolicy, OpCall, TTracingInst>;
        lookup[(int)Instruction.CALLCODE] = &InstructionCall<TGasPolicy, OpCallCode, TTracingInst>;
        lookup[(int)Instruction.RETURN] = &InstructionReturn;
        if (spec.DelegateCallEnabled)
        {
            lookup[(int)Instruction.DELEGATECALL] = &InstructionCall<TGasPolicy, OpDelegateCall, TTracingInst>;
        }
        if (spec.Create2OpcodeEnabled)
        {
            lookup[(int)Instruction.CREATE2] = &InstructionCreate<TGasPolicy, OpCreate2, TTracingInst>;
        }

        lookup[(int)Instruction.RETURNDATALOAD] = &InstructionReturnDataLoad<TGasPolicy, TTracingInst>;
        if (spec.StaticCallEnabled)
        {
            lookup[(int)Instruction.STATICCALL] = &InstructionCall<TGasPolicy, OpStaticCall, TTracingInst>;
        }

        // Extended call opcodes in EO mode.
        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.EXTCALL] = &InstructionEofCall<TGasPolicy, OpEofCall, TTracingInst>;
            if (spec.DelegateCallEnabled)
            {
                lookup[(int)Instruction.EXTDELEGATECALL] =
                    &InstructionEofCall<TGasPolicy, OpEofDelegateCall, TTracingInst>;
            }
            if (spec.StaticCallEnabled)
            {
                lookup[(int)Instruction.EXTSTATICCALL] = &InstructionEofCall<TGasPolicy, OpEofStaticCall, TTracingInst>;
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
