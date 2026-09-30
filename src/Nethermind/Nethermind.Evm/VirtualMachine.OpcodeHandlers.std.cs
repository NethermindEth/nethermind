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
        CarriesRegisters<TTracingInst>()
            ? AsTableEntry(&RawCalliHelper.ExecuteCarriedOpcode<TOpcode, TCancelable, OnFlag>)
            : &RawCalliHelper.ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OnFlag>;

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
