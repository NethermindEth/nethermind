// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

public partial class VirtualMachine<TGasPolicy>
{
    /// <summary>
    /// Fork-specialized opcode dispatch: a faithful, case-for-case transcription of
    /// <see cref="EvmInstructions.GenerateOpCodes{TGasPolicy, TTracingInst}"/> as a switch.
    /// Every TSpec gate below mirrors the corresponding table gate verbatim; because the flags
    /// are compile-time constants, the JIT folds each gate and dead-code-eliminates untaken
    /// cases, leaving per-fork code with DIRECT, inlinable call sites — replacing the table's
    /// single megamorphic calli, whose target the branch predictor cannot learn.
    ///
    /// The full Ethereum test suite running through both this path (tip forks) and the table
    /// path (all other specs) is the equivalence guard; EvmSpecGuardTests additionally lock
    /// every TSpec struct to its runtime fork, flag by flag.
    /// </summary>
    private EvmExceptionType DispatchSpecialized<TTracingInst, TSpec>(Instruction instruction, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TTracingInst : struct, IFlag
        where TSpec : struct, IEvmSpec
    {
        switch (instruction)
        {
            case Instruction.STOP:
                return EvmInstructions.InstructionStop(this, ref stack, ref gas, ref programCounter);
            case Instruction.ADD:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpAdd, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.MUL:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpMul, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SUB:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpSub, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DIV:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpDiv, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SDIV:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpSDiv, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.MOD:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpMod, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SMOD:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpSMod, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.ADDMOD:
                return EvmInstructions.InstructionMath3Param<TGasPolicy, EvmInstructions.OpAddMod, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.MULMOD:
                return EvmInstructions.InstructionMath3Param<TGasPolicy, EvmInstructions.OpMulMod, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.EXP:
                return EvmInstructions.InstructionExp<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SIGNEXTEND:
                return EvmInstructions.InstructionSignExtend(this, ref stack, ref gas, ref programCounter);

            case Instruction.LT:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpLt, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.GT:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpGt, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SLT:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpSLt, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SGT:
                return EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpSGt, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.EQ:
                return EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseEq>(this, ref stack, ref gas, ref programCounter);
            case Instruction.ISZERO:
                return EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpIsZero>(this, ref stack, ref gas, ref programCounter);
            case Instruction.AND:
                return EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseAnd>(this, ref stack, ref gas, ref programCounter);
            case Instruction.OR:
                return EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseOr>(this, ref stack, ref gas, ref programCounter);
            case Instruction.XOR:
                return EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseXor>(this, ref stack, ref gas, ref programCounter);
            case Instruction.NOT:
                return EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpNot>(this, ref stack, ref gas, ref programCounter);
            case Instruction.BYTE:
                return EvmInstructions.InstructionByte<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.SHL:
                if (!TSpec.ShiftOpcodesEnabled) goto default;
                return EvmInstructions.InstructionShift<TGasPolicy, EvmInstructions.OpShl, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SHR:
                if (!TSpec.ShiftOpcodesEnabled) goto default;
                return EvmInstructions.InstructionShift<TGasPolicy, EvmInstructions.OpShr, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SAR:
                if (!TSpec.ShiftOpcodesEnabled) goto default;
                return EvmInstructions.InstructionSar<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CLZ:
                if (!TSpec.CLZEnabled) goto default;
                return EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpCLZ>(this, ref stack, ref gas, ref programCounter);

            case Instruction.KECCAK256:
                return EvmInstructions.InstructionKeccak256<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.ADDRESS:
                return EvmInstructions.InstructionEnvAddress<TGasPolicy, EvmInstructions.OpAddress<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.BALANCE:
                return EvmInstructions.InstructionBalance<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.ORIGIN:
                return EvmInstructions.InstructionEnv32Bytes<TGasPolicy, EvmInstructions.OpOrigin<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CALLER:
                return EvmInstructions.InstructionEnvAddress<TGasPolicy, EvmInstructions.OpCaller<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CALLVALUE:
                return EvmInstructions.InstructionEnvUInt256<TGasPolicy, EvmInstructions.OpCallValue<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CALLDATALOAD:
                return EvmInstructions.InstructionCallDataLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CALLDATASIZE:
                return EvmInstructions.InstructionEnvUInt32<TGasPolicy, EvmInstructions.OpCallDataSize<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CALLDATACOPY:
                return EvmInstructions.InstructionCallDataCopy<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CODESIZE:
                return EvmInstructions.InstructionCodeSize<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CODECOPY:
                return EvmInstructions.InstructionCodeCopy<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.GASPRICE:
                return EvmInstructions.InstructionBlkUInt256<TGasPolicy, EvmInstructions.OpGasPrice<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.EXTCODESIZE:
                return EvmInstructions.InstructionExtCodeSize<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.EXTCODECOPY:
                return EvmInstructions.InstructionExtCodeCopy<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.RETURNDATASIZE:
                if (!TSpec.ReturnDataOpcodesEnabled) goto default;
                return EvmInstructions.InstructionReturnDataSize<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.RETURNDATACOPY:
                if (!TSpec.ReturnDataOpcodesEnabled) goto default;
                return EvmInstructions.InstructionReturnDataCopy<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.EXTCODEHASH:
                if (!TSpec.ExtCodeHashOpcodeEnabled) goto default;
                return EvmInstructions.InstructionExtCodeHash<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.BLOCKHASH:
                return EvmInstructions.InstructionBlockHash<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.COINBASE:
                return EvmInstructions.InstructionBlkAddress<TGasPolicy, EvmInstructions.OpCoinbase<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.TIMESTAMP:
                return EvmInstructions.InstructionBlkUInt64<TGasPolicy, EvmInstructions.OpTimestamp<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.NUMBER:
                return EvmInstructions.InstructionBlkUInt64<TGasPolicy, EvmInstructions.OpNumber<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PREVRANDAO:
                return EvmInstructions.InstructionPrevRandao<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.GASLIMIT:
                return EvmInstructions.InstructionBlkUInt64<TGasPolicy, EvmInstructions.OpGasLimit<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CHAINID:
                if (!TSpec.ChainIdOpcodeEnabled) goto default;
                return EvmInstructions.InstructionEnv32Bytes<TGasPolicy, EvmInstructions.OpChainId<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SELFBALANCE:
                if (!TSpec.SelfBalanceOpcodeEnabled) goto default;
                return EvmInstructions.InstructionSelfBalance<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.BASEFEE:
                if (!TSpec.BaseFeeEnabled) goto default;
                return EvmInstructions.InstructionBlkUInt256<TGasPolicy, EvmInstructions.OpBaseFee<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.BLOBHASH:
                if (!TSpec.IsEip4844Enabled) goto default;
                return EvmInstructions.InstructionBlobHash<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.BLOBBASEFEE:
                if (!TSpec.IsEip4844Enabled) goto default;
                return EvmInstructions.InstructionBlobBaseFee<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SLOTNUM:
                if (!TSpec.IsEip7843Enabled) goto default;
                return EvmInstructions.InstructionSlotNum<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.POP:
                return EvmInstructions.InstructionPop(this, ref stack, ref gas, ref programCounter);
            case Instruction.MLOAD:
                return EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.MSTORE:
                return EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.MSTORE8:
                return EvmInstructions.InstructionMStore8<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SLOAD:
                return EvmInstructions.InstructionSLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SSTORE:
                if (!TSpec.UseNetGasMetering)
                    return EvmInstructions.InstructionSStoreUnmetered<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
                if (TSpec.UseNetGasMeteringWithAStipendFix)
                {
                    return TSpec.IsEip8037Enabled
                        ? EvmInstructions.InstructionSStoreMetered<TGasPolicy, TTracingInst, OnFlag, OnFlag>(this, ref stack, ref gas, ref programCounter)
                        : EvmInstructions.InstructionSStoreMetered<TGasPolicy, TTracingInst, OnFlag, OffFlag>(this, ref stack, ref gas, ref programCounter);
                }
                return TSpec.IsEip8037Enabled
                    ? EvmInstructions.InstructionSStoreMetered<TGasPolicy, TTracingInst, OffFlag, OnFlag>(this, ref stack, ref gas, ref programCounter)
                    : EvmInstructions.InstructionSStoreMetered<TGasPolicy, TTracingInst, OffFlag, OffFlag>(this, ref stack, ref gas, ref programCounter);

            case Instruction.JUMP:
                return EvmInstructions.InstructionJump(this, ref stack, ref gas, ref programCounter);
            case Instruction.JUMPI:
                return EvmInstructions.InstructionJumpIf(this, ref stack, ref gas, ref programCounter);
            case Instruction.PC:
                return EvmInstructions.InstructionProgramCounter<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.MSIZE:
                return EvmInstructions.InstructionEnvUInt64<TGasPolicy, EvmInstructions.OpMSize<TGasPolicy>, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.GAS:
                return EvmInstructions.InstructionGas<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.JUMPDEST:
                return EvmInstructions.InstructionJumpDest(this, ref stack, ref gas, ref programCounter);

            case Instruction.TLOAD:
                if (!TSpec.TransientStorageEnabled) goto default;
                return EvmInstructions.InstructionTLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.TSTORE:
                if (!TSpec.TransientStorageEnabled) goto default;
                return EvmInstructions.InstructionTStore(this, ref stack, ref gas, ref programCounter);
            case Instruction.MCOPY:
                if (!TSpec.MCopyIncluded) goto default;
                return EvmInstructions.InstructionMCopy<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.PUSH0:
                if (!TSpec.IncludePush0Instruction) goto default;
                return EvmInstructions.InstructionPush0<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH1:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH2:
                return EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH3:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH4:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH5:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH6:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op6, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH7:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op7, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH8:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op8, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH9:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op9, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH10:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op10, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH11:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op11, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH12:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op12, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH13:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op13, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH14:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op14, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH15:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op15, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH16:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op16, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH17:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op17, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH18:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op18, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH19:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op19, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH20:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op20, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH21:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op21, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH22:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op22, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH23:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op23, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH24:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op24, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH25:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op25, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH26:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op26, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH27:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op27, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH28:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op28, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH29:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op29, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH30:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op30, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH31:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op31, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.PUSH32:
                return EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op32, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.DUP1:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP2:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP3:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP4:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP5:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP6:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op6, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP7:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op7, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP8:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op8, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP9:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op9, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP10:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op10, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP11:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op11, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP12:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op12, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP13:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op13, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP14:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op14, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP15:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op15, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.DUP16:
                return EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op16, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.SWAP1:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP2:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP3:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP4:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP5:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP6:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op6, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP7:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op7, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP8:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op8, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP9:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op9, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP10:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op10, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP11:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op11, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP12:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op12, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP13:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op13, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP14:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op14, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP15:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op15, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAP16:
                return EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op16, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.LOG0:
                return EvmInstructions.InstructionLog<TGasPolicy, EvmInstructions.Op0>(this, ref stack, ref gas, ref programCounter);
            case Instruction.LOG1:
                return EvmInstructions.InstructionLog<TGasPolicy, EvmInstructions.Op1>(this, ref stack, ref gas, ref programCounter);
            case Instruction.LOG2:
                return EvmInstructions.InstructionLog<TGasPolicy, EvmInstructions.Op2>(this, ref stack, ref gas, ref programCounter);
            case Instruction.LOG3:
                return EvmInstructions.InstructionLog<TGasPolicy, EvmInstructions.Op3>(this, ref stack, ref gas, ref programCounter);
            case Instruction.LOG4:
                return EvmInstructions.InstructionLog<TGasPolicy, EvmInstructions.Op4>(this, ref stack, ref gas, ref programCounter);

            case Instruction.DUPN:
                if (!TSpec.IsEip8024Enabled) goto default;
                return EvmInstructions.InstructionDupN<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.SWAPN:
                if (!TSpec.IsEip8024Enabled) goto default;
                return EvmInstructions.InstructionSwapN<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);
            case Instruction.EXCHANGE:
                if (!TSpec.IsEip8024Enabled) goto default;
                return EvmInstructions.InstructionExchange<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref programCounter);

            case Instruction.CREATE:
                return TSpec.IsEip8037Enabled
                    ? EvmInstructions.InstructionCreate<TGasPolicy, EvmInstructions.OpCreate, TTracingInst, OnFlag>(this, ref stack, ref gas, ref programCounter)
                    : EvmInstructions.InstructionCreate<TGasPolicy, EvmInstructions.OpCreate, TTracingInst, OffFlag>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CREATE2:
                if (!TSpec.Create2OpcodeEnabled) goto default;
                return TSpec.IsEip8037Enabled
                    ? EvmInstructions.InstructionCreate<TGasPolicy, EvmInstructions.OpCreate2, TTracingInst, OnFlag>(this, ref stack, ref gas, ref programCounter)
                    : EvmInstructions.InstructionCreate<TGasPolicy, EvmInstructions.OpCreate2, TTracingInst, OffFlag>(this, ref stack, ref gas, ref programCounter);
            case Instruction.CALL:
                return DispatchCallLike<TTracingInst, TSpec, EvmInstructions.OpCall>(ref stack, ref gas, ref programCounter);
            case Instruction.CALLCODE:
                return DispatchCallLike<TTracingInst, TSpec, EvmInstructions.OpCallCode>(ref stack, ref gas, ref programCounter);
            case Instruction.DELEGATECALL:
                if (!TSpec.DelegateCallEnabled) goto default;
                return DispatchCallLike<TTracingInst, TSpec, EvmInstructions.OpDelegateCall>(ref stack, ref gas, ref programCounter);
            case Instruction.STATICCALL:
                if (!TSpec.StaticCallEnabled) goto default;
                return DispatchCallLike<TTracingInst, TSpec, EvmInstructions.OpStaticCall>(ref stack, ref gas, ref programCounter);
            case Instruction.RETURN:
                return EvmInstructions.InstructionReturn(this, ref stack, ref gas, ref programCounter);
            case Instruction.REVERT:
                if (!TSpec.RevertOpcodeEnabled) goto default;
                return EvmInstructions.InstructionRevert(this, ref stack, ref gas, ref programCounter);
            case Instruction.INVALID:
                return EvmInstructions.InstructionInvalid(this, ref stack, ref gas, ref programCounter);
            case Instruction.SELFDESTRUCT:
                if (TSpec.IsEip8037Enabled)
                {
                    return TSpec.IsEip7708Enabled
                        ? EvmInstructions.InstructionSelfDestruct<TGasPolicy, OnFlag, OnFlag>(this, ref stack, ref gas, ref programCounter)
                        : EvmInstructions.InstructionSelfDestruct<TGasPolicy, OnFlag, OffFlag>(this, ref stack, ref gas, ref programCounter);
                }
                return TSpec.IsEip7708Enabled
                    ? EvmInstructions.InstructionSelfDestruct<TGasPolicy, OffFlag, OnFlag>(this, ref stack, ref gas, ref programCounter)
                    : EvmInstructions.InstructionSelfDestruct<TGasPolicy, OffFlag, OffFlag>(this, ref stack, ref gas, ref programCounter);

            default:
                return EvmInstructions.InstructionBadInstruction(this, ref stack, ref gas, ref programCounter);
        }
    }

    private EvmExceptionType DispatchCallLike<TTracingInst, TSpec, TOpCall>(ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TTracingInst : struct, IFlag
        where TSpec : struct, IEvmSpec
        where TOpCall : struct, EvmInstructions.IOpCall
    {
        if (TSpec.IsEip8037Enabled)
        {
            return TSpec.IsEip7708Enabled
                ? EvmInstructions.InstructionCall<TGasPolicy, TOpCall, TTracingInst, OnFlag, OnFlag>(this, ref stack, ref gas, ref programCounter)
                : EvmInstructions.InstructionCall<TGasPolicy, TOpCall, TTracingInst, OnFlag, OffFlag>(this, ref stack, ref gas, ref programCounter);
        }
        return TSpec.IsEip7708Enabled
            ? EvmInstructions.InstructionCall<TGasPolicy, TOpCall, TTracingInst, OffFlag, OnFlag>(this, ref stack, ref gas, ref programCounter)
            : EvmInstructions.InstructionCall<TGasPolicy, TOpCall, TTracingInst, OffFlag, OffFlag>(this, ref stack, ref gas, ref programCounter);
    }
}
