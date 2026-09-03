// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    // Per-spec opcode dispatch tables; only a handful of specs are ever active (at head, the current and
    // next fork). std-only: the zkEVM guest runs a single fork and caches in plain statics (see .zkevm).
    private sealed unsafe class OpcodeTable
    {
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>[]? ThreadedNoTrace;
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>[]? ThreadedNoTraceCancelable;
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>[]? ThreadedTraced;
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>[]? ThreadedTracedCancelable;

        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref ThreadedState, EvmExceptionType>[]
            GetThreaded<TTracingInst, TCancelable>(IReleaseSpec spec)
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
        {
            if (TTracingInst.IsActive)
            {
                return TCancelable.IsActive
                    ? ThreadedTracedCancelable ??= GenerateThreadedOpcodeTable<TTracingInst, TCancelable>(spec)
                    : ThreadedTraced ??= GenerateThreadedOpcodeTable<TTracingInst, TCancelable>(spec);
            }

            return TCancelable.IsActive
                ? ThreadedNoTraceCancelable ??= GenerateThreadedOpcodeTable<TTracingInst, TCancelable>(spec)
                : ThreadedNoTrace ??= GenerateThreadedOpcodeTable<TTracingInst, TCancelable>(spec);
        }
    }

    // Weak keys: transient state-override specs in eth_simulateV1 must not be retained forever by this
    // process-wide cache.
    private static readonly ConditionalWeakTable<IReleaseSpec, OpcodeTable> _opcodeTablesBySpec = [];

    public object ReturnData { get; set; }

    private partial void PrepareOpcodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag
    {
    }

    protected virtual delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref nint, EvmExceptionType>[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        EvmInstructions.GenerateOpCodes<TGasPolicy, TTracingInst>(spec);
}
