// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    // A table that carries the remaining gas and the stack head in registers holds only handlers of the wider
    // register signature (RawCalliHelper.ExecuteCarriedOpcode); the others keep the signature they declare.
    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>
        OpcodeHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        !CarriesRegisters<TTracingInst>()
            ? &RawCalliHelper.ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OnFlag>
            // PUSH2 fuses a following jump, which a copy of the stack would keep too many values live for.
            : typeof(TOpcode) == typeof(Push2Opcode<OffFlag>)
                ? AsTableEntry(&RawCalliHelper.ExecuteCarriedPush2<TCancelable>)
                : AsTableEntry(&RawCalliHelper.ExecuteCarriedOpcode<TOpcode, TCancelable, OnFlag>);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>
        TerminatingOpcodeHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        CarriesRegisters<TTracingInst>()
            ? AsTableEntry(&RawCalliHelper.ExecuteCarriedOpcode<TOpcode, TCancelable, OffFlag>)
            : &RawCalliHelper.ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OffFlag>;

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>
        JumpIfOpcodeHandler<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        CarriesRegisters<TTracingInst>()
            ? AsTableEntry(&RawCalliHelper.ExecuteCarriedJumpIf<TCancelable>)
            : &RawCalliHelper.ExecuteJumpIfOpcode<TTracingInst, TCancelable>;
}
