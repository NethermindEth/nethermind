// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    private interface IThreadedOpcode
    {
        static abstract EvmExceptionType Execute(
            VirtualMachine<TGasPolicy> vm,
            ref EvmStack stack,
            ref TGasPolicy gas,
            ref nint programCounter);
    }

    private static delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>
        ThreadedHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IThreadedOpcode
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        &ExecuteThreadedOpcode<TOpcode, TTracingInst, TCancelable>;

    private static delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>[]
        GenerateThreadedOpcodeTable<TTracingInst, TCancelable>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>[] lookup =
            new delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>[byte.MaxValue + 1];
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType> badInstruction =
            ThreadedHandler<BadInstructionOpcode, TTracingInst, TCancelable>();

        for (int i = 0; i < lookup.Length; i++)
            lookup[i] = badInstruction;

        lookup[(int)Instruction.STOP] = ThreadedHandler<StopOpcode, TTracingInst, TCancelable>();
        lookup[(int)Instruction.ADD] = ThreadedHandler<Math2Opcode<EvmInstructions.OpAdd, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MUL] = ThreadedHandler<Math2Opcode<EvmInstructions.OpMul, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SUB] = ThreadedHandler<Math2Opcode<EvmInstructions.OpSub, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DIV] = ThreadedHandler<Math2Opcode<EvmInstructions.OpDiv, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SDIV] = ThreadedHandler<Math2Opcode<EvmInstructions.OpSDiv, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MOD] = ThreadedHandler<Math2Opcode<EvmInstructions.OpMod, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SMOD] = ThreadedHandler<Math2Opcode<EvmInstructions.OpSMod, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.ADDMOD] = ThreadedHandler<Math3Opcode<EvmInstructions.OpAddMod, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MULMOD] = ThreadedHandler<Math3Opcode<EvmInstructions.OpMulMod, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.EXP] = ThreadedHandler<ExpOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SIGNEXTEND] = ThreadedHandler<SignExtendOpcode, TTracingInst, TCancelable>();

        lookup[(int)Instruction.LT] = ThreadedHandler<Math2Opcode<EvmInstructions.OpLt, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.GT] = ThreadedHandler<Math2Opcode<EvmInstructions.OpGt, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SLT] = ThreadedHandler<Math2Opcode<EvmInstructions.OpSLt, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SGT] = ThreadedHandler<Math2Opcode<EvmInstructions.OpSGt, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.EQ] = ThreadedHandler<BitwiseOpcode<EvmInstructions.OpBitwiseEq>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.ISZERO] = ThreadedHandler<Math1Opcode<EvmInstructions.OpIsZero>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.AND] = ThreadedHandler<BitwiseOpcode<EvmInstructions.OpBitwiseAnd>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.OR] = ThreadedHandler<BitwiseOpcode<EvmInstructions.OpBitwiseOr>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.XOR] = ThreadedHandler<BitwiseOpcode<EvmInstructions.OpBitwiseXor>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.NOT] = ThreadedHandler<Math1Opcode<EvmInstructions.OpNot>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.BYTE] = ThreadedHandler<ByteOpcode<TTracingInst>, TTracingInst, TCancelable>();

        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = ThreadedHandler<ShiftOpcode<EvmInstructions.OpShl, TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.SHR] = ThreadedHandler<ShiftOpcode<EvmInstructions.OpShr, TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.SAR] = ThreadedHandler<SarOpcode<TTracingInst>, TTracingInst, TCancelable>();
        }

        if (spec.CLZEnabled)
            lookup[(int)Instruction.CLZ] = ThreadedHandler<Math1Opcode<EvmInstructions.OpCLZ>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.KECCAK256] = ThreadedHandler<KeccakOpcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.ADDRESS] = ThreadedHandler<EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.BALANCE] = ThreadedHandler<BalanceOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.ORIGIN] = ThreadedHandler<Env32BytesOpcode<EvmInstructions.OpOrigin<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLER] = ThreadedHandler<EnvAddressOpcode<EvmInstructions.OpCaller<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLVALUE] = ThreadedHandler<EnvUInt256Opcode<EvmInstructions.OpCallValue<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLDATALOAD] = ThreadedHandler<CallDataLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLDATASIZE] = ThreadedHandler<EnvUInt32Opcode<EvmInstructions.OpCallDataSize<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLDATACOPY] = ThreadedHandler<CallDataCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CODESIZE] = ThreadedHandler<CodeSizeOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CODECOPY] = ThreadedHandler<CodeCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.GASPRICE] = ThreadedHandler<BlkUInt256Opcode<EvmInstructions.OpGasPrice<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.EXTCODESIZE] = ThreadedHandler<ExtCodeSizeOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.EXTCODECOPY] = ThreadedHandler<ExtCodeCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();

        if (spec.ReturnDataOpcodesEnabled)
        {
            lookup[(int)Instruction.RETURNDATASIZE] = ThreadedHandler<ReturnDataSizeOpcode<TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.RETURNDATACOPY] = ThreadedHandler<ReturnDataCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();
        }

        if (spec.ExtCodeHashOpcodeEnabled)
            lookup[(int)Instruction.EXTCODEHASH] = ThreadedHandler<ExtCodeHashOpcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.BLOCKHASH] = ThreadedHandler<BlockHashOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.COINBASE] = ThreadedHandler<BlkAddressOpcode<EvmInstructions.OpCoinbase<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.TIMESTAMP] = ThreadedHandler<BlkUInt64Opcode<EvmInstructions.OpTimestamp<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.NUMBER] = ThreadedHandler<BlkUInt64Opcode<EvmInstructions.OpNumber<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PREVRANDAO] = ThreadedHandler<PrevRandaoOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.GASLIMIT] = ThreadedHandler<BlkUInt64Opcode<EvmInstructions.OpGasLimit<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();

        if (spec.ChainIdOpcodeEnabled)
            lookup[(int)Instruction.CHAINID] = ThreadedHandler<Env32BytesOpcode<EvmInstructions.OpChainId<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        if (spec.SelfBalanceOpcodeEnabled)
            lookup[(int)Instruction.SELFBALANCE] = ThreadedHandler<SelfBalanceOpcode<TTracingInst>, TTracingInst, TCancelable>();
        if (spec.BaseFeeEnabled)
            lookup[(int)Instruction.BASEFEE] = ThreadedHandler<BlkUInt256Opcode<EvmInstructions.OpBaseFee<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        if (spec.IsEip4844Enabled)
            lookup[(int)Instruction.BLOBHASH] = ThreadedHandler<BlobHashOpcode<TTracingInst>, TTracingInst, TCancelable>();
        if (spec.BlobBaseFeeEnabled)
            lookup[(int)Instruction.BLOBBASEFEE] = ThreadedHandler<BlobBaseFeeOpcode<TTracingInst>, TTracingInst, TCancelable>();
        if (spec.IsEip7843Enabled)
            lookup[(int)Instruction.SLOTNUM] = ThreadedHandler<SlotNumOpcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.POP] = ThreadedHandler<PopOpcode, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MLOAD] = ThreadedHandler<MLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MSTORE] = ThreadedHandler<MStoreOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MSTORE8] = ThreadedHandler<MStore8Opcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SLOAD] = ThreadedHandler<SLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SSTORE] = spec.UseNetGasMetering
            ? spec.UseNetGasMeteringWithAStipendFix
                ? spec.IsEip8037Enabled
                    ? ThreadedHandler<SStoreMeteredOpcode<TTracingInst, OnFlag, OnFlag>, TTracingInst, TCancelable>()
                    : ThreadedHandler<SStoreMeteredOpcode<TTracingInst, OnFlag, OffFlag>, TTracingInst, TCancelable>()
                : spec.IsEip8037Enabled
                    ? ThreadedHandler<SStoreMeteredOpcode<TTracingInst, OffFlag, OnFlag>, TTracingInst, TCancelable>()
                    : ThreadedHandler<SStoreMeteredOpcode<TTracingInst, OffFlag, OffFlag>, TTracingInst, TCancelable>()
            : ThreadedHandler<SStoreUnmeteredOpcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.JUMP] = ThreadedHandler<JumpOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.JUMPI] = ThreadedHandler<JumpIfOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PC] = ThreadedHandler<ProgramCounterOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MSIZE] = ThreadedHandler<EnvUInt64Opcode<EvmInstructions.OpMSize<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.GAS] = ThreadedHandler<GasOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.JUMPDEST] = ThreadedHandler<JumpDestOpcode, TTracingInst, TCancelable>();

        if (spec.TransientStorageEnabled)
        {
            lookup[(int)Instruction.TLOAD] = ThreadedHandler<TLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.TSTORE] = ThreadedHandler<TStoreOpcode, TTracingInst, TCancelable>();
        }
        if (spec.MCopyIncluded)
            lookup[(int)Instruction.MCOPY] = ThreadedHandler<MCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();
        if (spec.IncludePush0Instruction)
            lookup[(int)Instruction.PUSH0] = ThreadedHandler<Push0Opcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.PUSH1] = ThreadedHandler<PushOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH2] = ThreadedHandler<Push2Opcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH3] = ThreadedHandler<PushOpcode<EvmInstructions.Op3, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH4] = ThreadedHandler<PushOpcode<EvmInstructions.Op4, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH5] = ThreadedHandler<PushOpcode<EvmInstructions.Op5, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH6] = ThreadedHandler<PushOpcode<EvmInstructions.Op6, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH7] = ThreadedHandler<PushOpcode<EvmInstructions.Op7, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH8] = ThreadedHandler<PushOpcode<EvmInstructions.Op8, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH9] = ThreadedHandler<PushOpcode<EvmInstructions.Op9, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH10] = ThreadedHandler<PushOpcode<EvmInstructions.Op10, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH11] = ThreadedHandler<PushOpcode<EvmInstructions.Op11, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH12] = ThreadedHandler<PushOpcode<EvmInstructions.Op12, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH13] = ThreadedHandler<PushOpcode<EvmInstructions.Op13, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH14] = ThreadedHandler<PushOpcode<EvmInstructions.Op14, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH15] = ThreadedHandler<PushOpcode<EvmInstructions.Op15, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH16] = ThreadedHandler<PushOpcode<EvmInstructions.Op16, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH17] = ThreadedHandler<PushOpcode<EvmInstructions.Op17, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH18] = ThreadedHandler<PushOpcode<EvmInstructions.Op18, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH19] = ThreadedHandler<PushOpcode<EvmInstructions.Op19, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH20] = ThreadedHandler<PushOpcode<EvmInstructions.Op20, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH21] = ThreadedHandler<PushOpcode<EvmInstructions.Op21, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH22] = ThreadedHandler<PushOpcode<EvmInstructions.Op22, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH23] = ThreadedHandler<PushOpcode<EvmInstructions.Op23, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH24] = ThreadedHandler<PushOpcode<EvmInstructions.Op24, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH25] = ThreadedHandler<PushOpcode<EvmInstructions.Op25, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH26] = ThreadedHandler<PushOpcode<EvmInstructions.Op26, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH27] = ThreadedHandler<PushOpcode<EvmInstructions.Op27, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH28] = ThreadedHandler<PushOpcode<EvmInstructions.Op28, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH29] = ThreadedHandler<PushOpcode<EvmInstructions.Op29, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH30] = ThreadedHandler<PushOpcode<EvmInstructions.Op30, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH31] = ThreadedHandler<PushOpcode<EvmInstructions.Op31, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH32] = ThreadedHandler<PushOpcode<EvmInstructions.Op32, TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.DUP1] = ThreadedHandler<DupOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP2] = ThreadedHandler<DupOpcode<EvmInstructions.Op2, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP3] = ThreadedHandler<DupOpcode<EvmInstructions.Op3, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP4] = ThreadedHandler<DupOpcode<EvmInstructions.Op4, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP5] = ThreadedHandler<DupOpcode<EvmInstructions.Op5, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP6] = ThreadedHandler<DupOpcode<EvmInstructions.Op6, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP7] = ThreadedHandler<DupOpcode<EvmInstructions.Op7, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP8] = ThreadedHandler<DupOpcode<EvmInstructions.Op8, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP9] = ThreadedHandler<DupOpcode<EvmInstructions.Op9, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP10] = ThreadedHandler<DupOpcode<EvmInstructions.Op10, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP11] = ThreadedHandler<DupOpcode<EvmInstructions.Op11, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP12] = ThreadedHandler<DupOpcode<EvmInstructions.Op12, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP13] = ThreadedHandler<DupOpcode<EvmInstructions.Op13, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP14] = ThreadedHandler<DupOpcode<EvmInstructions.Op14, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP15] = ThreadedHandler<DupOpcode<EvmInstructions.Op15, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP16] = ThreadedHandler<DupOpcode<EvmInstructions.Op16, TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.SWAP1] = ThreadedHandler<SwapOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP2] = ThreadedHandler<SwapOpcode<EvmInstructions.Op2, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP3] = ThreadedHandler<SwapOpcode<EvmInstructions.Op3, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP4] = ThreadedHandler<SwapOpcode<EvmInstructions.Op4, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP5] = ThreadedHandler<SwapOpcode<EvmInstructions.Op5, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP6] = ThreadedHandler<SwapOpcode<EvmInstructions.Op6, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP7] = ThreadedHandler<SwapOpcode<EvmInstructions.Op7, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP8] = ThreadedHandler<SwapOpcode<EvmInstructions.Op8, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP9] = ThreadedHandler<SwapOpcode<EvmInstructions.Op9, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP10] = ThreadedHandler<SwapOpcode<EvmInstructions.Op10, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP11] = ThreadedHandler<SwapOpcode<EvmInstructions.Op11, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP12] = ThreadedHandler<SwapOpcode<EvmInstructions.Op12, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP13] = ThreadedHandler<SwapOpcode<EvmInstructions.Op13, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP14] = ThreadedHandler<SwapOpcode<EvmInstructions.Op14, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP15] = ThreadedHandler<SwapOpcode<EvmInstructions.Op15, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP16] = ThreadedHandler<SwapOpcode<EvmInstructions.Op16, TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.LOG0] = ThreadedHandler<LogOpcode<EvmInstructions.Op0>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.LOG1] = ThreadedHandler<LogOpcode<EvmInstructions.Op1>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.LOG2] = ThreadedHandler<LogOpcode<EvmInstructions.Op2>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.LOG3] = ThreadedHandler<LogOpcode<EvmInstructions.Op3>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.LOG4] = ThreadedHandler<LogOpcode<EvmInstructions.Op4>, TTracingInst, TCancelable>();

        if (spec.IsEip8024Enabled)
        {
            lookup[(int)Instruction.DUPN] = ThreadedHandler<DupNOpcode<TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.SWAPN] = ThreadedHandler<SwapNOpcode<TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.EXCHANGE] = ThreadedHandler<ExchangeOpcode<TTracingInst>, TTracingInst, TCancelable>();
        }

        lookup[(int)Instruction.CREATE] = spec.IsEip8037Enabled
            ? ThreadedHandler<CreateOpcode<EvmInstructions.OpCreate, TTracingInst, OnFlag>, TTracingInst, TCancelable>()
            : ThreadedHandler<CreateOpcode<EvmInstructions.OpCreate, TTracingInst, OffFlag>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALL] = GetCallHandler<EvmInstructions.OpCall, TTracingInst, TCancelable>(spec);
        lookup[(int)Instruction.CALLCODE] = GetCallHandler<EvmInstructions.OpCallCode, TTracingInst, TCancelable>(spec);
        lookup[(int)Instruction.RETURN] = ThreadedHandler<ReturnOpcode, TTracingInst, TCancelable>();
        if (spec.DelegateCallEnabled)
            lookup[(int)Instruction.DELEGATECALL] = GetCallHandler<EvmInstructions.OpDelegateCall, TTracingInst, TCancelable>(spec);
        if (spec.Create2OpcodeEnabled)
        {
            lookup[(int)Instruction.CREATE2] = spec.IsEip8037Enabled
                ? ThreadedHandler<CreateOpcode<EvmInstructions.OpCreate2, TTracingInst, OnFlag>, TTracingInst, TCancelable>()
                : ThreadedHandler<CreateOpcode<EvmInstructions.OpCreate2, TTracingInst, OffFlag>, TTracingInst, TCancelable>();
        }
        if (spec.StaticCallEnabled)
            lookup[(int)Instruction.STATICCALL] = GetCallHandler<EvmInstructions.OpStaticCall, TTracingInst, TCancelable>(spec);
        if (spec.RevertOpcodeEnabled)
            lookup[(int)Instruction.REVERT] = ThreadedHandler<RevertOpcode, TTracingInst, TCancelable>();

        lookup[(int)Instruction.INVALID] = ThreadedHandler<InvalidOpcode, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SELFDESTRUCT] = (spec.IsEip8037Enabled, spec.IsEip7708Enabled) switch
        {
            (true, true) => ThreadedHandler<SelfDestructOpcode<OnFlag, OnFlag>, TTracingInst, TCancelable>(),
            (true, false) => ThreadedHandler<SelfDestructOpcode<OnFlag, OffFlag>, TTracingInst, TCancelable>(),
            (false, true) => ThreadedHandler<SelfDestructOpcode<OffFlag, OnFlag>, TTracingInst, TCancelable>(),
            (false, false) => ThreadedHandler<SelfDestructOpcode<OffFlag, OffFlag>, TTracingInst, TCancelable>(),
        };

        return lookup;
    }

    private static delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>
        GetCallHandler<TOpCall, TTracingInst, TCancelable>(IReleaseSpec spec)
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        (spec.IsEip8037Enabled, spec.IsEip7708Enabled) switch
        {
            (true, true) => ThreadedHandler<CallOpcode<TOpCall, TTracingInst, OnFlag, OnFlag>, TTracingInst, TCancelable>(),
            (true, false) => ThreadedHandler<CallOpcode<TOpCall, TTracingInst, OnFlag, OffFlag>, TTracingInst, TCancelable>(),
            (false, true) => ThreadedHandler<CallOpcode<TOpCall, TTracingInst, OffFlag, OnFlag>, TTracingInst, TCancelable>(),
            (false, false) => ThreadedHandler<CallOpcode<TOpCall, TTracingInst, OffFlag, OffFlag>, TTracingInst, TCancelable>(),
        };

    private readonly struct BadInstructionOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBadInstruction(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct StopOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionStop(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct Math2Opcode<TOpMath, TTracingInst> : IThreadedOpcode
        where TOpMath : struct, EvmInstructions.IOpMath2Param
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionMath2Param<TGasPolicy, TOpMath, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct Math3Opcode<TOpMath, TTracingInst> : IThreadedOpcode
        where TOpMath : struct, EvmInstructions.IOpMath3Param
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionMath3Param<TGasPolicy, TOpMath, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct Math1Opcode<TOpMath> : IThreadedOpcode where TOpMath : struct, EvmInstructions.IOpMath1Param
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionMath1Param<TGasPolicy, TOpMath>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct BitwiseOpcode<TOpBitwise> : IThreadedOpcode where TOpBitwise : struct, EvmInstructions.IOpBitwise
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBitwise<TGasPolicy, TOpBitwise>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ExpOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionExp<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SignExtendOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSignExtend(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ByteOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionByte<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ShiftOpcode<TOpShift, TTracingInst> : IThreadedOpcode
        where TOpShift : struct, EvmInstructions.IOpShift
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionShift<TGasPolicy, TOpShift, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SarOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSar<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct KeccakOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionKeccak256<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct EnvAddressOpcode<TOpEnv, TTracingInst> : IThreadedOpcode
        where TOpEnv : struct, EvmInstructions.IOpEnvAddress<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionEnvAddress<TGasPolicy, TOpEnv, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct Env32BytesOpcode<TOpEnv, TTracingInst> : IThreadedOpcode
        where TOpEnv : struct, EvmInstructions.IOpEnv32Bytes<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionEnv32Bytes<TGasPolicy, TOpEnv, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct EnvUInt256Opcode<TOpEnv, TTracingInst> : IThreadedOpcode
        where TOpEnv : struct, EvmInstructions.IOpEnvUInt256<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionEnvUInt256<TGasPolicy, TOpEnv, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct EnvUInt32Opcode<TOpEnv, TTracingInst> : IThreadedOpcode
        where TOpEnv : struct, EvmInstructions.IOpEnvUInt32<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionEnvUInt32<TGasPolicy, TOpEnv, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct EnvUInt64Opcode<TOpEnv, TTracingInst> : IThreadedOpcode
        where TOpEnv : struct, EvmInstructions.IOpEnvUInt64<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionEnvUInt64<TGasPolicy, TOpEnv, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct BlkAddressOpcode<TOpEnv, TTracingInst> : IThreadedOpcode
        where TOpEnv : struct, EvmInstructions.IOpBlkAddress<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBlkAddress<TGasPolicy, TOpEnv, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct BlkUInt256Opcode<TOpEnv, TTracingInst> : IThreadedOpcode
        where TOpEnv : struct, EvmInstructions.IOpBlkUInt256<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBlkUInt256<TGasPolicy, TOpEnv, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct BlkUInt64Opcode<TOpEnv, TTracingInst> : IThreadedOpcode
        where TOpEnv : struct, EvmInstructions.IOpBlkUInt64<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBlkUInt64<TGasPolicy, TOpEnv, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct BalanceOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBalance<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct CallDataLoadOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionCallDataLoad<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct CallDataCopyOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionCallDataCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct CodeSizeOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionCodeSize<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct CodeCopyOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionCodeCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ExtCodeSizeOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionExtCodeSize<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ExtCodeCopyOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionExtCodeCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ReturnDataSizeOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionReturnDataSize<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ReturnDataCopyOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionReturnDataCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ExtCodeHashOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionExtCodeHash<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct BlockHashOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBlockHash<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct PrevRandaoOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionPrevRandao<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SelfBalanceOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSelfBalance<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct BlobHashOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBlobHash<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct BlobBaseFeeOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionBlobBaseFee<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SlotNumOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSlotNum<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct PopOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionPop(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct MLoadOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct MStoreOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct MStore8Opcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionMStore8<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SLoadOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSLoad<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SStoreMeteredOpcode<TTracingInst, TStipendFix, TEip8037> : IThreadedOpcode
        where TTracingInst : struct, IFlag
        where TStipendFix : struct, IFlag
        where TEip8037 : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSStoreMetered<TGasPolicy, TTracingInst, TStipendFix, TEip8037>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SStoreUnmeteredOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSStoreUnmetered<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct JumpOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            TTracingInst.IsActive
                ? EvmInstructions.InstructionJump(vm, ref stack, ref gas, ref programCounter)
                : EvmInstructions.InstructionJumpAndSkipJumpDest(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct JumpIfOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            TTracingInst.IsActive
                ? EvmInstructions.InstructionJumpIf(vm, ref stack, ref gas, ref programCounter)
                : EvmInstructions.InstructionJumpIfAndSkipJumpDest(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ProgramCounterOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionProgramCounter<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct GasOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionGas<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct JumpDestOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionJumpDest(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct TLoadOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionTLoad<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct TStoreOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionTStore(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct MCopyOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionMCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct Push0Opcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionPush0<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct Push2Opcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct PushOpcode<TOpCount, TTracingInst> : IThreadedOpcode
        where TOpCount : struct, EvmInstructions.IOpCount
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionPush<TGasPolicy, TOpCount, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct DupOpcode<TOpCount, TTracingInst> : IThreadedOpcode
        where TOpCount : struct, EvmInstructions.IOpCount
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionDup<TGasPolicy, TOpCount, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SwapOpcode<TOpCount, TTracingInst> : IThreadedOpcode
        where TOpCount : struct, EvmInstructions.IOpCount
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSwap<TGasPolicy, TOpCount, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct LogOpcode<TOpCount> : IThreadedOpcode where TOpCount : struct, EvmInstructions.IOpCount
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionLog<TGasPolicy, TOpCount>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct DupNOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionDupN<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SwapNOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSwapN<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ExchangeOpcode<TTracingInst> : IThreadedOpcode where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionExchange<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct CreateOpcode<TOpCreate, TTracingInst, TEip8037> : IThreadedOpcode
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where TTracingInst : struct, IFlag
        where TEip8037 : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionCreate<TGasPolicy, TOpCreate, TTracingInst, TEip8037>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct CallOpcode<TOpCall, TTracingInst, TEip8037, TEip7708> : IThreadedOpcode
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TEip8037 : struct, IFlag
        where TEip7708 : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionCall<TGasPolicy, TOpCall, TTracingInst, TEip8037, TEip7708>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct ReturnOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionReturn(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct RevertOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionRevert(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct InvalidOpcode : IThreadedOpcode
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionInvalid(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SelfDestructOpcode<TEip8037, TEip7708> : IThreadedOpcode
        where TEip8037 : struct, IFlag
        where TEip7708 : struct, IFlag
    {
        public static EvmExceptionType Execute(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter) =>
            EvmInstructions.InstructionSelfDestruct<TGasPolicy, TEip8037, TEip7708>(vm, ref stack, ref gas, ref programCounter);
    }
}
