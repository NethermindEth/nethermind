// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <summary>One opcode's semantics, shared by every dispatch table that contains it.</summary>
    /// <remarks>
    /// The leading parameters match the dispatch signature, so a handler's arguments already sit in the
    /// registers its body wants and only the virtual machine has to be loaded out of the dispatch state.
    /// The counter stays by reference at this layer because these forwarders always inline, so it never
    /// becomes address-taken. A body that never moves it does not receive it at all, and the jump bodies,
    /// which do not inline, return it in an <see cref="OpcodeResult"/> rather than writing it back.
    /// </remarks>
    private interface IOpcodeBody
    {
        /// <summary>Whether dispatch owns the gas/depth guards and Execute cannot fail or access the VM.</summary>
        static virtual bool HasCheckedBody => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static virtual bool TryConsumeGas(ref TGasPolicy gas) => false;
        static virtual int StackInputs => 0;

        static abstract EvmExceptionType Execute(
            ref EvmStack stack,
            ref TGasPolicy gas,
            VirtualMachine<TGasPolicy> vm,
            ref nint programCounter);
    }

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        OpcodeHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        &ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OnFlag>;

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        TerminatingOpcodeHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        &ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OffFlag>;

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        JumpIfOpcodeHandler<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        &ExecuteJumpIfOpcode<TTracingInst, TCancelable>;

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[]
        GenerateOpcodeHandlers<TTracingInst, TCancelable>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[] lookup =
            new delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[byte.MaxValue + 1];
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType> badInstruction =
            TerminatingOpcodeHandler<BadInstructionOpcode, TTracingInst, TCancelable>();

        for (int i = 0; i < lookup.Length; i++)
            lookup[i] = badInstruction;

        lookup[(int)Instruction.STOP] = TerminatingOpcodeHandler<StopOpcode, TTracingInst, TCancelable>();
        if (SpecFlags.Eip2929(spec))
            ConfigureAccessOpcodes<TTracingInst, TCancelable, OnFlag>(lookup, spec);
        else
            ConfigureAccessOpcodes<TTracingInst, TCancelable, OffFlag>(lookup, spec);

        lookup[(int)Instruction.ADD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpAdd, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MUL] = OpcodeHandler<Math2Opcode<EvmInstructions.OpMul, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SUB] = OpcodeHandler<Math2Opcode<EvmInstructions.OpSub, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DIV] = OpcodeHandler<Math2Opcode<EvmInstructions.OpDiv, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SDIV] = OpcodeHandler<Math2Opcode<EvmInstructions.OpSDiv, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MOD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpMod, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SMOD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpSMod, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.ADDMOD] = OpcodeHandler<Math3Opcode<EvmInstructions.OpAddMod, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MULMOD] = OpcodeHandler<Math3Opcode<EvmInstructions.OpMulMod, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.EXP] = OpcodeHandler<ExpOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SIGNEXTEND] = OpcodeHandler<SignExtendOpcode, TTracingInst, TCancelable>();

        lookup[(int)Instruction.LT] = OpcodeHandler<Math2Opcode<EvmInstructions.OpLt, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.GT] = OpcodeHandler<Math2Opcode<EvmInstructions.OpGt, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SLT] = OpcodeHandler<Math2Opcode<EvmInstructions.OpSLt, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SGT] = OpcodeHandler<Math2Opcode<EvmInstructions.OpSGt, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.EQ] = OpcodeHandler<BitwiseOpcode<EvmInstructions.OpBitwiseEq, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.ISZERO] = OpcodeHandler<Math1Opcode<EvmInstructions.OpIsZero, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.AND] = OpcodeHandler<BitwiseOpcode<EvmInstructions.OpBitwiseAnd, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.OR] = OpcodeHandler<BitwiseOpcode<EvmInstructions.OpBitwiseOr, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.XOR] = OpcodeHandler<BitwiseOpcode<EvmInstructions.OpBitwiseXor, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.NOT] = OpcodeHandler<Math1Opcode<EvmInstructions.OpNot, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.BYTE] = OpcodeHandler<ByteOpcode<TTracingInst>, TTracingInst, TCancelable>();

        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = OpcodeHandler<ShiftOpcode<EvmInstructions.OpShl, TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.SHR] = OpcodeHandler<ShiftOpcode<EvmInstructions.OpShr, TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.SAR] = OpcodeHandler<SarOpcode<TTracingInst>, TTracingInst, TCancelable>();
        }

        if (spec.CLZEnabled)
            lookup[(int)Instruction.CLZ] = OpcodeHandler<CountLeadingZerosOpcode, TTracingInst, TCancelable>();

        lookup[(int)Instruction.KECCAK256] = OpcodeHandler<KeccakOpcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.ADDRESS] = OpcodeHandler<EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.ORIGIN] = OpcodeHandler<Env32BytesOpcode<EvmInstructions.OpOrigin<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLER] = OpcodeHandler<EnvAddressOpcode<EvmInstructions.OpCaller<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLVALUE] = OpcodeHandler<EnvUInt256Opcode<EvmInstructions.OpCallValue<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLDATALOAD] = OpcodeHandler<CallDataLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLDATASIZE] = OpcodeHandler<EnvUInt32Opcode<EvmInstructions.OpCallDataSize<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CALLDATACOPY] = OpcodeHandler<CallDataCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CODESIZE] = OpcodeHandler<CodeSizeOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.CODECOPY] = OpcodeHandler<CodeCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.GASPRICE] = OpcodeHandler<BlkUInt256Opcode<EvmInstructions.OpGasPrice<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();

        if (spec.ReturnDataOpcodesEnabled)
        {
            lookup[(int)Instruction.RETURNDATASIZE] = OpcodeHandler<ReturnDataSizeOpcode<TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.RETURNDATACOPY] = OpcodeHandler<ReturnDataCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();
        }

        lookup[(int)Instruction.BLOCKHASH] = OpcodeHandler<BlockHashOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.COINBASE] = OpcodeHandler<BlkAddressOpcode<EvmInstructions.OpCoinbase<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.TIMESTAMP] = OpcodeHandler<BlkUInt64Opcode<EvmInstructions.OpTimestamp<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.NUMBER] = OpcodeHandler<BlkUInt64Opcode<EvmInstructions.OpNumber<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PREVRANDAO] = OpcodeHandler<PrevRandaoOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.GASLIMIT] = OpcodeHandler<BlkUInt64Opcode<EvmInstructions.OpGasLimit<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();

        if (spec.ChainIdOpcodeEnabled)
            lookup[(int)Instruction.CHAINID] = OpcodeHandler<Env32BytesOpcode<EvmInstructions.OpChainId<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        if (spec.SelfBalanceOpcodeEnabled)
            lookup[(int)Instruction.SELFBALANCE] = OpcodeHandler<SelfBalanceOpcode<TTracingInst>, TTracingInst, TCancelable>();
        if (spec.BaseFeeEnabled)
            lookup[(int)Instruction.BASEFEE] = OpcodeHandler<BlkUInt256Opcode<EvmInstructions.OpBaseFee<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        if (spec.IsEip4844Enabled)
            lookup[(int)Instruction.BLOBHASH] = OpcodeHandler<BlobHashOpcode<TTracingInst>, TTracingInst, TCancelable>();
        if (spec.BlobBaseFeeEnabled)
            lookup[(int)Instruction.BLOBBASEFEE] = OpcodeHandler<BlobBaseFeeOpcode<TTracingInst>, TTracingInst, TCancelable>();
        if (spec.IsEip7843Enabled)
            lookup[(int)Instruction.SLOTNUM] = OpcodeHandler<SlotNumOpcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.POP] = OpcodeHandler<PopOpcode, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MLOAD] = OpcodeHandler<MLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MSTORE] = OpcodeHandler<MStoreOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MSTORE8] = OpcodeHandler<MStore8Opcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.JUMP] = OpcodeHandler<JumpOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.JUMPI] = JumpIfOpcodeHandler<TTracingInst, TCancelable>();
        lookup[(int)Instruction.PC] = OpcodeHandler<ProgramCounterOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.MSIZE] = OpcodeHandler<EnvUInt64Opcode<EvmInstructions.OpMSize<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.GAS] = OpcodeHandler<GasOpcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.JUMPDEST] = OpcodeHandler<JumpDestOpcode, TTracingInst, TCancelable>();

        if (spec.TransientStorageEnabled)
        {
            lookup[(int)Instruction.TLOAD] = OpcodeHandler<TLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.TSTORE] = OpcodeHandler<TStoreOpcode, TTracingInst, TCancelable>();
        }
        if (spec.MCopyIncluded)
            lookup[(int)Instruction.MCOPY] = OpcodeHandler<MCopyOpcode<TTracingInst>, TTracingInst, TCancelable>();
        if (spec.IncludePush0Instruction)
            lookup[(int)Instruction.PUSH0] = OpcodeHandler<Push0Opcode<TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.PUSH1] = OpcodeHandler<PushOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH2] = OpcodeHandler<Push2Opcode<TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH3] = OpcodeHandler<PushOpcode<EvmInstructions.Op3, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH4] = OpcodeHandler<PushOpcode<EvmInstructions.Op4, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH5] = OpcodeHandler<PushOpcode<EvmInstructions.Op5, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH6] = OpcodeHandler<PushOpcode<EvmInstructions.Op6, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH7] = OpcodeHandler<PushOpcode<EvmInstructions.Op7, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH8] = OpcodeHandler<PushOpcode<EvmInstructions.Op8, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH9] = OpcodeHandler<PushOpcode<EvmInstructions.Op9, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH10] = OpcodeHandler<PushOpcode<EvmInstructions.Op10, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH11] = OpcodeHandler<PushOpcode<EvmInstructions.Op11, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH12] = OpcodeHandler<PushOpcode<EvmInstructions.Op12, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH13] = OpcodeHandler<PushOpcode<EvmInstructions.Op13, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH14] = OpcodeHandler<PushOpcode<EvmInstructions.Op14, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH15] = OpcodeHandler<PushOpcode<EvmInstructions.Op15, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH16] = OpcodeHandler<PushOpcode<EvmInstructions.Op16, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH17] = OpcodeHandler<PushOpcode<EvmInstructions.Op17, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH18] = OpcodeHandler<PushOpcode<EvmInstructions.Op18, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH19] = OpcodeHandler<PushOpcode<EvmInstructions.Op19, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH20] = OpcodeHandler<PushOpcode<EvmInstructions.Op20, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH21] = OpcodeHandler<PushOpcode<EvmInstructions.Op21, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH22] = OpcodeHandler<PushOpcode<EvmInstructions.Op22, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH23] = OpcodeHandler<PushOpcode<EvmInstructions.Op23, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH24] = OpcodeHandler<PushOpcode<EvmInstructions.Op24, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH25] = OpcodeHandler<PushOpcode<EvmInstructions.Op25, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH26] = OpcodeHandler<PushOpcode<EvmInstructions.Op26, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH27] = OpcodeHandler<PushOpcode<EvmInstructions.Op27, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH28] = OpcodeHandler<PushOpcode<EvmInstructions.Op28, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH29] = OpcodeHandler<PushOpcode<EvmInstructions.Op29, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH30] = OpcodeHandler<PushOpcode<EvmInstructions.Op30, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH31] = OpcodeHandler<PushOpcode<EvmInstructions.Op31, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.PUSH32] = OpcodeHandler<PushOpcode<EvmInstructions.Op32, TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.DUP1] = OpcodeHandler<DupOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP2] = OpcodeHandler<DupOpcode<EvmInstructions.Op2, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP3] = OpcodeHandler<DupOpcode<EvmInstructions.Op3, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP4] = OpcodeHandler<DupOpcode<EvmInstructions.Op4, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP5] = OpcodeHandler<DupOpcode<EvmInstructions.Op5, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP6] = OpcodeHandler<DupOpcode<EvmInstructions.Op6, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP7] = OpcodeHandler<DupOpcode<EvmInstructions.Op7, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP8] = OpcodeHandler<DupOpcode<EvmInstructions.Op8, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP9] = OpcodeHandler<DupOpcode<EvmInstructions.Op9, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP10] = OpcodeHandler<DupOpcode<EvmInstructions.Op10, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP11] = OpcodeHandler<DupOpcode<EvmInstructions.Op11, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP12] = OpcodeHandler<DupOpcode<EvmInstructions.Op12, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP13] = OpcodeHandler<DupOpcode<EvmInstructions.Op13, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP14] = OpcodeHandler<DupOpcode<EvmInstructions.Op14, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP15] = OpcodeHandler<DupOpcode<EvmInstructions.Op15, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.DUP16] = OpcodeHandler<DupOpcode<EvmInstructions.Op16, TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.SWAP1] = OpcodeHandler<SwapOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP2] = OpcodeHandler<SwapOpcode<EvmInstructions.Op2, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP3] = OpcodeHandler<SwapOpcode<EvmInstructions.Op3, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP4] = OpcodeHandler<SwapOpcode<EvmInstructions.Op4, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP5] = OpcodeHandler<SwapOpcode<EvmInstructions.Op5, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP6] = OpcodeHandler<SwapOpcode<EvmInstructions.Op6, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP7] = OpcodeHandler<SwapOpcode<EvmInstructions.Op7, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP8] = OpcodeHandler<SwapOpcode<EvmInstructions.Op8, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP9] = OpcodeHandler<SwapOpcode<EvmInstructions.Op9, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP10] = OpcodeHandler<SwapOpcode<EvmInstructions.Op10, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP11] = OpcodeHandler<SwapOpcode<EvmInstructions.Op11, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP12] = OpcodeHandler<SwapOpcode<EvmInstructions.Op12, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP13] = OpcodeHandler<SwapOpcode<EvmInstructions.Op13, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP14] = OpcodeHandler<SwapOpcode<EvmInstructions.Op14, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP15] = OpcodeHandler<SwapOpcode<EvmInstructions.Op15, TTracingInst>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SWAP16] = OpcodeHandler<SwapOpcode<EvmInstructions.Op16, TTracingInst>, TTracingInst, TCancelable>();

        lookup[(int)Instruction.LOG0] = OpcodeHandler<LogOpcode<EvmInstructions.Op0>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.LOG1] = OpcodeHandler<LogOpcode<EvmInstructions.Op1>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.LOG2] = OpcodeHandler<LogOpcode<EvmInstructions.Op2>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.LOG3] = OpcodeHandler<LogOpcode<EvmInstructions.Op3>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.LOG4] = OpcodeHandler<LogOpcode<EvmInstructions.Op4>, TTracingInst, TCancelable>();

        if (spec.IsEip8024Enabled)
        {
            lookup[(int)Instruction.DUPN] = OpcodeHandler<DupNOpcode<TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.SWAPN] = OpcodeHandler<SwapNOpcode<TTracingInst>, TTracingInst, TCancelable>();
            lookup[(int)Instruction.EXCHANGE] = OpcodeHandler<ExchangeOpcode<TTracingInst>, TTracingInst, TCancelable>();
        }

        lookup[(int)Instruction.CREATE] = GetCreateHandler<EvmInstructions.OpCreate, TTracingInst, TCancelable>(spec);
        lookup[(int)Instruction.CALL] = GetCallHandler<EvmInstructions.OpCall, TTracingInst, TCancelable>(spec);
        lookup[(int)Instruction.CALLCODE] = GetCallHandler<EvmInstructions.OpCallCode, TTracingInst, TCancelable>(spec);
        lookup[(int)Instruction.RETURN] = TerminatingOpcodeHandler<ReturnOpcode, TTracingInst, TCancelable>();
        if (spec.DelegateCallEnabled)
            lookup[(int)Instruction.DELEGATECALL] = GetCallHandler<EvmInstructions.OpDelegateCall, TTracingInst, TCancelable>(spec);
        if (spec.Create2OpcodeEnabled)
        {
            lookup[(int)Instruction.CREATE2] = GetCreateHandler<EvmInstructions.OpCreate2, TTracingInst, TCancelable>(spec);
        }
        if (spec.StaticCallEnabled)
            lookup[(int)Instruction.STATICCALL] = GetCallHandler<EvmInstructions.OpStaticCall, TTracingInst, TCancelable>(spec);
        if (spec.RevertOpcodeEnabled)
            lookup[(int)Instruction.REVERT] = TerminatingOpcodeHandler<RevertOpcode, TTracingInst, TCancelable>();

        lookup[(int)Instruction.INVALID] = TerminatingOpcodeHandler<InvalidOpcode, TTracingInst, TCancelable>();

        return lookup;
    }

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCallHandler<TOpCall, TTracingInst, TCancelable>(IReleaseSpec spec)
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        SpecFlags.Eip2929(spec)
            ? GetCallHandler<TOpCall, TTracingInst, TCancelable, OnFlag>(spec)
            : GetCallHandler<TOpCall, TTracingInst, TCancelable, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929>(IReleaseSpec spec)
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag =>
        SpecFlags.Eip150(spec)
            ? GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, OnFlag>(spec)
            : GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150>(IReleaseSpec spec)
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag =>
        SpecFlags.Eip158(spec)
            ? GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, OnFlag>(spec)
            : GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, Eip158>(IReleaseSpec spec)
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip158 : struct, IFlag =>
        SpecFlags.Eip2780(spec)
            ? GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, Eip158, OnFlag>(spec)
            : GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, Eip158, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, Eip158, Eip2780>(IReleaseSpec spec)
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip158 : struct, IFlag
        where Eip2780 : struct, IFlag =>
        SpecFlags.Eip8038(spec)
            ? GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, Eip158, Eip2780, OnFlag>(spec)
            : GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, Eip158, Eip2780, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCallHandler<TOpCall, TTracingInst, TCancelable, Eip2929, Eip150, Eip158, Eip2780, Eip8038>(IReleaseSpec spec)
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip158 : struct, IFlag
        where Eip2780 : struct, IFlag
        where Eip8038 : struct, IFlag =>
        (SpecFlags.Eip8037(spec), SpecFlags.Eip7708(spec)) switch
        {
            (true, true) => OpcodeHandler<CallOpcode<TOpCall, TTracingInst, OnFlag, OnFlag, EvmInstructions.CallSpec<Eip2929, Eip150, Eip158, Eip2780, Eip8038>>, TTracingInst, TCancelable>(),
            (true, false) => OpcodeHandler<CallOpcode<TOpCall, TTracingInst, OnFlag, OffFlag, EvmInstructions.CallSpec<Eip2929, Eip150, Eip158, Eip2780, Eip8038>>, TTracingInst, TCancelable>(),
            (false, true) => OpcodeHandler<CallOpcode<TOpCall, TTracingInst, OffFlag, OnFlag, EvmInstructions.CallSpec<Eip2929, Eip150, Eip158, Eip2780, Eip8038>>, TTracingInst, TCancelable>(),
            (false, false) => OpcodeHandler<CallOpcode<TOpCall, TTracingInst, OffFlag, OffFlag, EvmInstructions.CallSpec<Eip2929, Eip150, Eip158, Eip2780, Eip8038>>, TTracingInst, TCancelable>(),
        };

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCreateHandler<TOpCreate, TTracingInst, TCancelable>(IReleaseSpec spec)
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        SpecFlags.Eip2929(spec)
            ? GetCreateHandler<TOpCreate, TTracingInst, TCancelable, OnFlag>(spec)
            : GetCreateHandler<TOpCreate, TTracingInst, TCancelable, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929>(IReleaseSpec spec)
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag =>
        SpecFlags.Eip150(spec)
            ? GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, OnFlag>(spec)
            : GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, Eip150>(IReleaseSpec spec)
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag =>
        SpecFlags.Eip3860(spec)
            ? GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, Eip150, OnFlag>(spec)
            : GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, Eip150, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, Eip150, Eip3860>(IReleaseSpec spec)
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip3860 : struct, IFlag =>
        SpecFlags.Eip8038(spec)
            ? GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, Eip150, Eip3860, OnFlag>(spec)
            : GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, Eip150, Eip3860, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetCreateHandler<TOpCreate, TTracingInst, TCancelable, Eip2929, Eip150, Eip3860, Eip8038>(IReleaseSpec spec)
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip3860 : struct, IFlag
        where Eip8038 : struct, IFlag =>
        SpecFlags.Eip8037(spec)
            ? OpcodeHandler<CreateOpcode<TOpCreate, TTracingInst, OnFlag, EvmInstructions.CreateSpec<Eip2929, Eip150, Eip3860, Eip8038>>, TTracingInst, TCancelable>()
            : OpcodeHandler<CreateOpcode<TOpCreate, TTracingInst, OffFlag, EvmInstructions.CreateSpec<Eip2929, Eip150, Eip3860, Eip8038>>, TTracingInst, TCancelable>();

    private static void ConfigureAccessOpcodes<TTracingInst, TCancelable, Eip2929>(delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[] lookup, IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
    {
        if (SpecFlags.Eip8038(spec))
            ConfigureAccessOpcodes<TTracingInst, TCancelable, Eip2929, OnFlag>(lookup, spec);
        else
            ConfigureAccessOpcodes<TTracingInst, TCancelable, Eip2929, OffFlag>(lookup, spec);
    }

    private static void ConfigureAccessOpcodes<TTracingInst, TCancelable, Eip2929, Eip8038>(delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[] lookup, IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip2929 : struct, IFlag
        where Eip8038 : struct, IFlag
    {
        lookup[(int)Instruction.BALANCE] = OpcodeHandler<BalanceOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.EXTCODESIZE] = OpcodeHandler<ExtCodeSizeOpcode<TTracingInst, Eip8038, Eip2929>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.EXTCODECOPY] = OpcodeHandler<ExtCodeCopyOpcode<TTracingInst, Eip8038, Eip2929>, TTracingInst, TCancelable>();
        if (spec.ExtCodeHashOpcodeEnabled)
            lookup[(int)Instruction.EXTCODEHASH] = OpcodeHandler<ExtCodeHashOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SLOAD] = OpcodeHandler<SLoadOpcode<TTracingInst, Eip8038, Eip2929>, TTracingInst, TCancelable>();
        lookup[(int)Instruction.SSTORE] = SStoreOpcodeHandler<TTracingInst, TCancelable, Eip8038, Eip2929>(spec);
        lookup[(int)Instruction.SELFDESTRUCT] =
            GetSelfDestructHandler<TTracingInst, TCancelable, EvmInstructions.AccessSpec<Eip2929, Eip8038>, Eip8038>(spec);
    }

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TAccess : struct, EvmInstructions.IAccessSpec
        where Eip8038 : struct, IFlag =>
        spec.UseShanghaiDDosProtection
            ? GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, OnFlag>(spec)
            : GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TAccess : struct, EvmInstructions.IAccessSpec
        where Eip8038 : struct, IFlag
        where Eip150 : struct, IFlag =>
        SpecFlags.Eip158(spec)
            ? GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, OnFlag>(spec)
            : GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, Eip158>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TAccess : struct, EvmInstructions.IAccessSpec
        where Eip8038 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip158 : struct, IFlag =>
        spec.SelfdestructOnlyOnSameTransaction
            ? GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, Eip158, OnFlag>(spec)
            : GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, Eip158, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, Eip158, Eip6780>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TAccess : struct, EvmInstructions.IAccessSpec
        where Eip8038 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip158 : struct, IFlag
        where Eip6780 : struct, IFlag =>
        spec.RemoveSelfdestructBurn
            ? GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, Eip158, Eip6780, OnFlag>(spec)
            : GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, Eip158, Eip6780, OffFlag>(spec);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        GetSelfDestructHandler<TTracingInst, TCancelable, TAccess, Eip8038, Eip150, Eip158, Eip6780, Eip8246>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TAccess : struct, EvmInstructions.IAccessSpec
        where Eip8038 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip158 : struct, IFlag
        where Eip6780 : struct, IFlag
        where Eip8246 : struct, IFlag =>
        (SpecFlags.Eip8037(spec), SpecFlags.Eip7708(spec)) switch
        {
            (true, true) => TerminatingOpcodeHandler<SelfDestructOpcode<OnFlag, OnFlag, EvmInstructions.SelfDestructSpec<TAccess, Eip150, Eip158, Eip6780, Eip8246, Eip8038>>, TTracingInst, TCancelable>(),
            (true, false) => TerminatingOpcodeHandler<SelfDestructOpcode<OnFlag, OffFlag, EvmInstructions.SelfDestructSpec<TAccess, Eip150, Eip158, Eip6780, Eip8246, Eip8038>>, TTracingInst, TCancelable>(),
            (false, true) => TerminatingOpcodeHandler<SelfDestructOpcode<OffFlag, OnFlag, EvmInstructions.SelfDestructSpec<TAccess, Eip150, Eip158, Eip6780, Eip8246, Eip8038>>, TTracingInst, TCancelable>(),
            (false, false) => TerminatingOpcodeHandler<SelfDestructOpcode<OffFlag, OffFlag, EvmInstructions.SelfDestructSpec<TAccess, Eip150, Eip158, Eip6780, Eip8246, Eip8038>>, TTracingInst, TCancelable>(),
        };

    private readonly struct BadInstructionOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionBadInstruction(ref stack, ref gas, vm);
    }

    private readonly struct StopOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionStop(ref stack, ref gas, vm);
    }

    private readonly struct Math2Opcode<TOpMath, TTracingInst> : IOpcodeBody
        where TOpMath : struct, EvmInstructions.IOpMath2Param
        where TTracingInst : struct, IFlag
    {
        public static bool HasCheckedBody
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !TTracingInst.IsActive;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryConsumeGas(ref TGasPolicy gas) => TGasPolicy.UpdateGas<TOpMath>(ref gas);
        public static int StackInputs => 2;

        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter)
        {
            if (!HasCheckedBody)
                return EvmInstructions.InstructionMath2Param<TGasPolicy, TOpMath, TTracingInst>(ref stack, ref gas);

            return EvmInstructions.Math2ParamCore<TOpMath, TTracingInst, OffFlag>(ref stack);
        }
    }

    private readonly struct Math3Opcode<TOpMath, TTracingInst> : IOpcodeBody
        where TOpMath : struct, EvmInstructions.IOpMath3Param
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionMath3Param<TGasPolicy, TOpMath, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct Math1Opcode<TOpMath, TTracingInst> : IOpcodeBody
        where TOpMath : struct, EvmInstructions.IOpMath1Param
        where TTracingInst : struct, IFlag
    {
        public static bool HasCheckedBody
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !TTracingInst.IsActive;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryConsumeGas(ref TGasPolicy gas) => TGasPolicy.UpdateGas<TOpMath>(ref gas);
        public static int StackInputs => 1;

        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter)
        {
            if (!HasCheckedBody)
                return EvmInstructions.InstructionMath1Param<TGasPolicy, TOpMath>(ref stack, ref gas, vm);

            return EvmInstructions.Math1ParamCore<TOpMath, OffFlag>(ref stack);
        }
    }

    private readonly struct BitwiseOpcode<TOpBitwise, TTracingInst> : IOpcodeBody
        where TOpBitwise : struct, EvmInstructions.IOpBitwise
        where TTracingInst : struct, IFlag
    {
        public static bool HasCheckedBody
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !TTracingInst.IsActive;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryConsumeGas(ref TGasPolicy gas) => TGasPolicy.UpdateGas<TOpBitwise>(ref gas);
        public static int StackInputs => 2;

        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter)
        {
            if (!HasCheckedBody)
                return EvmInstructions.InstructionBitwise<TGasPolicy, TOpBitwise>(ref stack, ref gas, vm);

            return EvmInstructions.BitwiseCore<TOpBitwise, OffFlag>(ref stack);
        }
    }

    private readonly struct ExpOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionExp<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct SignExtendOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSignExtend(ref stack, ref gas, vm);
    }

    private readonly struct CountLeadingZerosOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionCountLeadingZeros<TGasPolicy>(ref stack, ref gas);
    }

    private readonly struct ByteOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionByte<TGasPolicy, TTracingInst>(ref stack, ref gas);
    }

    private readonly struct ShiftOpcode<TOpShift, TTracingInst> : IOpcodeBody
        where TOpShift : struct, EvmInstructions.IOpShift
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionShift<TGasPolicy, TOpShift, TTracingInst>(ref stack, ref gas);
    }

    private readonly struct SarOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSar<TGasPolicy, TTracingInst>(ref stack, ref gas);
    }

    private readonly struct KeccakOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionKeccak256<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct EnvAddressOpcode<TOpEnv, TTracingInst> : IOpcodeBody
        where TOpEnv : struct, EvmInstructions.IOpEnvAddress<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionEnvAddress<TGasPolicy, TOpEnv, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct Env32BytesOpcode<TOpEnv, TTracingInst> : IOpcodeBody
        where TOpEnv : struct, EvmInstructions.IOpEnv32Bytes<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionEnv32Bytes<TGasPolicy, TOpEnv, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct EnvUInt256Opcode<TOpEnv, TTracingInst> : IOpcodeBody
        where TOpEnv : struct, EvmInstructions.IOpEnvUInt256<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionEnvUInt256<TGasPolicy, TOpEnv, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct EnvUInt32Opcode<TOpEnv, TTracingInst> : IOpcodeBody
        where TOpEnv : struct, EvmInstructions.IOpEnvUInt32<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionEnvUInt32<TGasPolicy, TOpEnv, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct EnvUInt64Opcode<TOpEnv, TTracingInst> : IOpcodeBody
        where TOpEnv : struct, EvmInstructions.IOpEnvUInt64<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionEnvUInt64<TGasPolicy, TOpEnv, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct BlkAddressOpcode<TOpEnv, TTracingInst> : IOpcodeBody
        where TOpEnv : struct, EvmInstructions.IOpBlkAddress<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionBlkAddress<TGasPolicy, TOpEnv, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct BlkUInt256Opcode<TOpEnv, TTracingInst> : IOpcodeBody
        where TOpEnv : struct, EvmInstructions.IOpBlkUInt256<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionBlkUInt256<TGasPolicy, TOpEnv, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct BlkUInt64Opcode<TOpEnv, TTracingInst> : IOpcodeBody
        where TOpEnv : struct, EvmInstructions.IOpBlkUInt64<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionBlkUInt64<TGasPolicy, TOpEnv, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct BalanceOpcode<TTracingInst, TSpec> : IOpcodeBody where TTracingInst : struct, IFlag
        where TSpec : struct, EvmInstructions.IAccessSpec
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionBalance<TGasPolicy, TTracingInst, TSpec>(ref stack, ref gas, vm);
    }

    private readonly struct CallDataLoadOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionCallDataLoad<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct CallDataCopyOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionCallDataCopy<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct CodeSizeOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionCodeSize<TGasPolicy, TTracingInst>(ref stack, ref gas);
    }

    private readonly struct CodeCopyOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionCodeCopy<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct ExtCodeSizeOpcode<TTracingInst, Eip8038, Eip2929> : IOpcodeBody
        where TTracingInst : struct, IFlag
        where Eip8038 : struct, IFlag
        where Eip2929 : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter)
        {
            OpcodeResult result = EvmInstructions.InstructionExtCodeSize<TGasPolicy, TTracingInst, Eip8038, Eip2929>(ref stack, ref gas, vm, programCounter);
            programCounter = result.ProgramCounter;
            return result.Exception;
        }
    }

    private readonly struct ExtCodeCopyOpcode<TTracingInst, Eip8038, Eip2929> : IOpcodeBody
        where TTracingInst : struct, IFlag
        where Eip8038 : struct, IFlag
        where Eip2929 : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionExtCodeCopy<TGasPolicy, TTracingInst, Eip8038, Eip2929>(ref stack, ref gas, vm);
    }

    private readonly struct ReturnDataSizeOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionReturnDataSize<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct ReturnDataCopyOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionReturnDataCopy<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct ExtCodeHashOpcode<TTracingInst, TSpec> : IOpcodeBody where TTracingInst : struct, IFlag
        where TSpec : struct, EvmInstructions.IAccessSpec
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionExtCodeHash<TGasPolicy, TTracingInst, TSpec>(ref stack, ref gas, vm);
    }

    private readonly struct BlockHashOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionBlockHash<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct PrevRandaoOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionPrevRandao<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct SelfBalanceOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSelfBalance<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct BlobHashOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionBlobHash<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct BlobBaseFeeOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionBlobBaseFee<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct SlotNumOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSlotNum<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct PopOpcode : IOpcodeBody
    {
        public static bool HasCheckedBody => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryConsumeGas(ref TGasPolicy gas) => TGasPolicy.UpdateGas<GasPolicy.BaseGasCost>(ref gas);
        public static int StackInputs => 1;

        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter)
        {
            stack.Head--;
            return EvmExceptionType.None;
        }
    }

    private readonly struct MLoadOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct MStoreOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct MStore8Opcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionMStore8<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        SStoreOpcodeHandler<TTracingInst, TCancelable, Eip8038, Eip2929>(IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where Eip8038 : struct, IFlag
        where Eip2929 : struct, IFlag =>
        spec.UseNetGasMetering
            ? spec.UseNetGasMeteringWithAStipendFix
                ? SpecFlags.Eip8037(spec)
                    ? OpcodeHandler<SStoreMeteredOpcode<TTracingInst, OnFlag, OnFlag, Eip8038, Eip2929>, TTracingInst, TCancelable>()
                    : OpcodeHandler<SStoreMeteredOpcode<TTracingInst, OnFlag, OffFlag, Eip8038, Eip2929>, TTracingInst, TCancelable>()
                : SpecFlags.Eip8037(spec)
                    ? OpcodeHandler<SStoreMeteredOpcode<TTracingInst, OffFlag, OnFlag, Eip8038, Eip2929>, TTracingInst, TCancelable>()
                    : OpcodeHandler<SStoreMeteredOpcode<TTracingInst, OffFlag, OffFlag, Eip8038, Eip2929>, TTracingInst, TCancelable>()
            : OpcodeHandler<SStoreUnmeteredOpcode<TTracingInst, Eip8038, Eip2929>, TTracingInst, TCancelable>();

    private readonly struct SLoadOpcode<TTracingInst, Eip8038, Eip2929> : IOpcodeBody
        where TTracingInst : struct, IFlag
        where Eip8038 : struct, IFlag
        where Eip2929 : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSLoad<TGasPolicy, TTracingInst, Eip8038, Eip2929>(ref stack, ref gas, vm);
    }

    private readonly struct SStoreMeteredOpcode<TTracingInst, TStipendFix, TEip8037, Eip8038, Eip2929> : IOpcodeBody
        where TTracingInst : struct, IFlag
        where TStipendFix : struct, IFlag
        where TEip8037 : struct, IFlag
        where Eip8038 : struct, IFlag
        where Eip2929 : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSStoreMetered<TGasPolicy, TTracingInst, TStipendFix, TEip8037, Eip8038, Eip2929>(ref stack, ref gas, vm);
    }

    private readonly struct SStoreUnmeteredOpcode<TTracingInst, Eip8038, Eip2929> : IOpcodeBody
        where TTracingInst : struct, IFlag
        where Eip8038 : struct, IFlag
        where Eip2929 : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSStoreUnmetered<TGasPolicy, TTracingInst, Eip8038, Eip2929>(ref stack, ref gas, vm);
    }

    private readonly struct JumpOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter)
        {
            OpcodeResult result = TTracingInst.IsActive
                ? EvmInstructions.InstructionJump(ref stack, ref gas, vm, programCounter)
                : EvmInstructions.InstructionJumpAndSkipJumpDest(ref stack, ref gas, vm, programCounter);
            programCounter = result.ProgramCounter;
            return result.Exception;
        }
    }

    private readonly struct ProgramCounterOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionProgramCounter<TGasPolicy, TTracingInst>(ref stack, ref gas, vm, programCounter);
    }

    private readonly struct GasOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionGas<TGasPolicy, TTracingInst>(ref stack, ref gas);
    }

    private readonly struct JumpDestOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionJumpDest(ref stack, ref gas, vm);
    }

    private readonly struct TLoadOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionTLoad<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct TStoreOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionTStore(ref stack, ref gas, vm);
    }

    private readonly struct MCopyOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionMCopy<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct Push0Opcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionPush0<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct Push2Opcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(ref stack, ref gas, vm, ref programCounter);
    }

    private readonly struct PushOpcode<TOpCount, TTracingInst> : IOpcodeBody
        where TOpCount : struct, EvmInstructions.IOpCount
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionPush<TGasPolicy, TOpCount, TTracingInst>(ref stack, ref gas, ref programCounter);
    }

    private readonly struct DupOpcode<TOpCount, TTracingInst> : IOpcodeBody
        where TOpCount : struct, EvmInstructions.IOpCount
        where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionDup<TGasPolicy, TOpCount, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct SwapOpcode<TOpCount, TTracingInst> : IOpcodeBody
        where TOpCount : struct, EvmInstructions.IOpCount
        where TTracingInst : struct, IFlag
    {
        public static bool HasCheckedBody
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !TTracingInst.IsActive;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryConsumeGas(ref TGasPolicy gas) => TGasPolicy.UpdateGas<GasPolicy.VeryLowGasCost>(ref gas);
        public static int StackInputs => TOpCount.Count + 1;

        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            HasCheckedBody
                ? stack.Swap<TTracingInst, OffFlag>(TOpCount.Count + 1)
                : EvmInstructions.InstructionSwap<TGasPolicy, TOpCount, TTracingInst>(ref stack, ref gas, vm);
    }

    private readonly struct LogOpcode<TOpCount> : IOpcodeBody where TOpCount : struct, EvmInstructions.IOpCount
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionLog<TGasPolicy, TOpCount>(ref stack, ref gas, vm);
    }

    private readonly struct DupNOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionDupN<TGasPolicy, TTracingInst>(ref stack, ref gas, ref programCounter);
    }

    private readonly struct SwapNOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSwapN<TGasPolicy, TTracingInst>(ref stack, ref gas, ref programCounter);
    }

    private readonly struct ExchangeOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionExchange<TGasPolicy, TTracingInst>(ref stack, ref gas, ref programCounter);
    }

    private readonly struct CreateOpcode<TOpCreate, TTracingInst, TEip8037, TSpec> : IOpcodeBody
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where TTracingInst : struct, IFlag
        where TEip8037 : struct, IFlag
        where TSpec : struct, EvmInstructions.ICreateSpec
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionCreate<TGasPolicy, TOpCreate, TTracingInst, TEip8037, TSpec>(ref stack, ref gas, vm);
    }

    private readonly struct CallOpcode<TOpCall, TTracingInst, TEip8037, TEip7708, TSpec> : IOpcodeBody
        where TOpCall : struct, EvmInstructions.IOpCall
        where TTracingInst : struct, IFlag
        where TEip8037 : struct, IFlag
        where TEip7708 : struct, IFlag
        where TSpec : struct, EvmInstructions.ICallSpec
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionCall<TGasPolicy, TOpCall, TTracingInst, TEip8037, TEip7708, TSpec>(ref stack, ref gas, vm);
    }

    private readonly struct ReturnOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionReturn(ref stack, ref gas, vm);
    }

    private readonly struct RevertOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionRevert(ref stack, ref gas, vm);
    }

    private readonly struct InvalidOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionInvalid(ref stack, ref gas, vm);
    }

    private readonly struct SelfDestructOpcode<TEip8037, TEip7708, TSpec> : IOpcodeBody
        where TEip8037 : struct, IFlag
        where TEip7708 : struct, IFlag
        where TSpec : struct, EvmInstructions.ISelfDestructSpec
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSelfDestruct<TGasPolicy, TEip8037, TEip7708, TSpec>(ref stack, ref gas, vm);
    }
}
