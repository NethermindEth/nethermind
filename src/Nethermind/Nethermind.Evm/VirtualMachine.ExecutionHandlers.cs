// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    private ExecutionHandlers? _executionHandlers;

    [MethodImpl(ExecutionHandlersInlining)]
    private ExecutionHandlers GetExecutionHandlers()
    {
        Debug.Assert(_executionHandlers is not null, "PrepareOpcodes resolves frame handlers before execution.");
#if DEBUG
        Debug.Assert(ReferenceEquals(_executionHandlers.Spec, Spec), "Frame handlers must belong to the current block spec.");
#endif
        return _executionHandlers!;
    }

    /// <summary>Resolves the untraced dispatch table and the current fork's own frame handlers, for tests that enter a frame directly.</summary>
    /// <remarks>The shared table keeps the frame handlers of the first fork it prepares, so they are rebuilt for the current spec.</remarks>
    internal void PrepareFrameHandlersForTests()
    {
        PrepareOpcodes<OffFlag>();
        _executionHandlers = new ExecutionHandlers(Spec);
    }

    /// <summary>Frame operations selected once for the same spec as the opcode tables.</summary>
    private sealed class ExecutionHandlers(IReleaseSpec spec)
    {
#if DEBUG
        public readonly IReleaseSpec Spec = spec;
#endif
        // All targets have these managed signatures; the table captures no VM or transaction state.
        public readonly delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, bool> InitializeFrame = SelectInitializeFrame(spec);
        public readonly delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, void> TransferLog =
            spec.IsEip7708Enabled ? &AddTransferLogCore<OnFlag> : &AddTransferLogCore<OffFlag>;
        public readonly delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, CallResult> RunPrecompile =
            (SpecFlags.Eip158(spec), SpecFlags.Eip8360(spec)) switch
            {
                (true, true) => &RunPrecompileCore<OnFlag, OnFlag>,
                (true, false) => &RunPrecompileCore<OnFlag, OffFlag>,
                (false, true) => &RunPrecompileCore<OffFlag, OnFlag>,
                (false, false) => &RunPrecompileCore<OffFlag, OffFlag>,
            };
        public readonly delegate*<VirtualMachine<TGasPolicy>, ref TGasPolicy, long, bool, void> CreditStateGasRefund =
            spec.IsEip8037Enabled ? &CreditStateGasRefundCore<OnFlag> : &CreditStateGasRefundCore<OffFlag>;
    }

    /// <summary>Selects how a frame credits and, under EIP-158, bumps the nonce of the account it executes in.</summary>
    private static delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, bool> SelectInitializeFrame(IReleaseSpec spec) =>
        // EIP-8360 frame entry also sets the TCREATE context, so it cannot be skipped.
        SpecFlags.Eip158(spec) && !SpecFlags.Eip8360(spec) ? &InitializeFrameSkippingNoOpCredit :
        SelectInitializeFrameCore(spec);

    /// <summary>The frame entry both builds select from, by the EIP-158 and EIP-8360 flags.</summary>
    private static delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, bool> SelectInitializeFrameCore(IReleaseSpec spec) =>
        (SpecFlags.Eip158(spec), SpecFlags.Eip8360(spec)) switch
        {
            (true, true) => &InitializeFrameCore<OnFlag, OnFlag>,
            (true, false) => &InitializeFrameCore<OnFlag, OffFlag>,
            (false, true) => &InitializeFrameCore<OffFlag, OnFlag>,
            (false, false) => &InitializeFrameCore<OffFlag, OffFlag>,
        };

    /// <returns><see langword="false"/> when the frame cannot pay its EIP-8360 balance-change charges.</returns>
    [SkipLocalsInit]
    private static bool InitializeFrameCore<Eip158, Eip8360>(VirtualMachine<TGasPolicy> vm, VmState<TGasPolicy> state)
        where Eip158 : struct, IFlag
        where Eip8360 : struct, IFlag
    {
        if (Eip8360.IsActive && !vm.TryEnterTransientCreateFrame(state))
            return false;

        ExecutionEnvironment env = state.Env;
        vm._worldState.AddToBalanceAndCreateIfNotEmpty(env.ExecutingAccount, state.ExecutionType, in env.Value, vm.Spec);
        // EIP-8360 gives a TCREATE account nonce 1 whatever EIP-161 says; IsTransientCreate relies on it.
        if ((Eip158.IsActive || (Eip8360.IsActive && state.ExecutionType == ExecutionType.TCREATE)) && state.ExecutionType.IsAnyCreate())
            vm._worldState.IncrementNonce(env.ExecutingAccount);
        return true;
    }

    /// <summary>
    /// <see cref="InitializeFrameCore{Eip158, Eip8360}"/> under EIP-158, minus the zero credit to the executing account of a frame that runs code.
    /// </summary>
    /// <remarks>
    /// That account is never empty: under CALL, STATICCALL and a transaction it holds the code, or an EIP-7702 designator
    /// to it; under DELEGATECALL and CALLCODE it is the caller, itself running code or a creation whose nonce is already 1.
    /// Crediting it zero neither creates nor touches it, yet costs two account lookups on every value-less call.
    /// Under EIP-7928 all the credit leaves in the block access list is the account's entry, which is already there: the
    /// call or transaction that targets the account made it while resolving the account's code, and under DELEGATECALL
    /// and CALLCODE the account is the caller's, whose entry was made the same way or by its creation.
    /// </remarks>
    private static bool InitializeFrameSkippingNoOpCredit(VirtualMachine<TGasPolicy> vm, VmState<TGasPolicy> state)
    {
        ExecutionEnvironment env = state.Env;
        ExecutionType executionType = state.ExecutionType;
        if (!executionType.IsAnyCreate() && env.CodeInfo.CodeLength != 0 && executionType.GetBalanceCredit(in env.Value).IsZero)
            return true;

        return InitializeFrameCore<OnFlag, OffFlag>(vm, state);
    }

    private static void AddTransferLogCore<Eip7708>(VirtualMachine<TGasPolicy> vm, VmState<TGasPolicy> state)
        where Eip7708 : struct, IFlag
    {
        if (Eip7708.IsActive && state.ExecutionType is not (ExecutionType.DELEGATECALL or ExecutionType.CALLCODE))
            vm.AddTransferLog<Eip7708>(state.From, state.To, in state.Env.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static CallResult RunPrecompileCore<Eip158, Eip8360>(VirtualMachine<TGasPolicy> vm, VmState<TGasPolicy> state)
        where Eip158 : struct, IFlag
        where Eip8360 : struct, IFlag => vm.RunPrecompile<Eip158, Eip8360>(state);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CreditStateGasRefundCore<Eip8037>(VirtualMachine<TGasPolicy> vm, ref TGasPolicy gas, long amount, bool trackSpillRefund)
        where Eip8037 : struct, IFlag => vm.CreditStateGasRefund<Eip8037>(ref gas, amount, trackSpillRefund);
}
