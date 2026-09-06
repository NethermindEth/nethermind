// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    private ExecutionHandlers? _executionHandlers;

    private ExecutionHandlers GetExecutionHandlers() =>
        _executionHandlers ??= GetOpcodeTable().GetExecutionHandlers(Spec);

    /// <summary>Frame operations selected once for the same spec as the opcode tables.</summary>
    private sealed class ExecutionHandlers(IReleaseSpec spec)
    {
        // All targets have these managed signatures; the table captures no VM or transaction state.
        public readonly delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, void> InitializeFrame =
            spec.ClearEmptyAccountWhenTouched ? &InitializeFrameCore<OnFlag> : &InitializeFrameCore<OffFlag>;
        public readonly delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, void> TransferLog =
            spec.IsEip7708Enabled ? &AddTransferLogCore<OnFlag> : &AddTransferLogCore<OffFlag>;
        public readonly delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, CallResult> RunPrecompile =
            spec.ClearEmptyAccountWhenTouched ? &RunPrecompileCore<OnFlag> : &RunPrecompileCore<OffFlag>;
        public readonly delegate*<VirtualMachine<TGasPolicy>, ref TGasPolicy, long, bool, void> CreditStateGasRefund =
            spec.IsEip8037Enabled ? &CreditStateGasRefundCore<OnFlag> : &CreditStateGasRefundCore<OffFlag>;
    }

    private static void InitializeFrameCore<Eip158>(VirtualMachine<TGasPolicy> vm, VmState<TGasPolicy> state)
        where Eip158 : struct, IFlag
    {
        ExecutionEnvironment env = state.Env;
        vm._worldState.AddToBalanceAndCreateIfNotExists(env.ExecutingAccount, state.ExecutionType, in env.Value, vm.Spec);
        if (Eip158.IsActive && state.ExecutionType.IsAnyCreate())
            vm._worldState.IncrementNonce(env.ExecutingAccount);
    }

    private static void AddTransferLogCore<Eip7708>(VirtualMachine<TGasPolicy> vm, VmState<TGasPolicy> state)
        where Eip7708 : struct, IFlag
    {
        if (Eip7708.IsActive && state.ExecutionType is not (ExecutionType.DELEGATECALL or ExecutionType.CALLCODE))
            vm.AddTransferLog<Eip7708>(state.From, state.To, in state.Env.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static CallResult RunPrecompileCore<Eip158>(VirtualMachine<TGasPolicy> vm, VmState<TGasPolicy> state)
        where Eip158 : struct, IFlag => vm.RunPrecompile<Eip158>(state);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CreditStateGasRefundCore<Eip8037>(VirtualMachine<TGasPolicy> vm, ref TGasPolicy gas, long amount, bool trackSpillRefund)
        where Eip8037 : struct, IFlag => vm.CreditStateGasRefund<Eip8037>(ref gas, amount, trackSpillRefund);
}
