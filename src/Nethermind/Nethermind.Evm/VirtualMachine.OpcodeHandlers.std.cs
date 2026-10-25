// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    // Untraced entries record the body they run while DescribeBodies builds a table, so a frame code runner sees the
    // interpreter's own per-fork choice of bodies.

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>
        OpcodeHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType> handler =
            AsTableEntry(&RawCalliHelper.ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OnFlag>);
        if (!TTracingInst.IsActive)
            RecordBody<TOpcode>((nint)handler, OpcodeFlow.Continues);
        return handler;
    }

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>
        TerminatingOpcodeHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType> handler =
            AsTableEntry(&RawCalliHelper.ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OffFlag>);
        if (!TTracingInst.IsActive)
            RecordBody<TOpcode>((nint)handler, OpcodeFlow.Terminates);
        return handler;
    }

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>
        JumpIfOpcodeHandler<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType> handler =
            AsTableEntry(&RawCalliHelper.ExecuteJumpIfOpcode<TTracingInst, TCancelable>);
        if (!TTracingInst.IsActive)
            RecordJumpIf((nint)handler);
        return handler;
    }
}
