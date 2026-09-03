// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private static long _txCount;
    private delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref nint, EvmExceptionType>[] _opcodeMethods;

    // Per-spec opcode dispatch tables; only a handful of specs are ever active (at head, the current and
    // next fork). std-only: the zkEVM guest runs a single fork and caches in plain statics (see .zkevm).
    private sealed unsafe class OpcodeTable
    {
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref nint, EvmExceptionType>[]? NoTrace;
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref nint, EvmExceptionType>[]? Traced;
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
        if (!StreamInterpreter.Enabled || !StreamInterpreter.ForceAllContexts)
            return;

        OpcodeTable table = _opcodeTablesBySpec.GetValue(spec, static _ => new OpcodeTable());

        // Check if tracing instructions are inactive.
        if (!TTracingInst.IsActive)
        {
            // Occasionally refresh the opcode cache for non-tracing opcodes.
            // The cache is flushed every 10,000 transactions until a threshold of 500,000 transactions.
            // This is to have the function pointers directly point at any PGO optimized methods rather than via pre-stubs
            // May be a few cycles to pick up pointers to the re-Jitted optimized methods depending on what's in the blocks,
            // however the the refreshes don't take long. (re-Jitting doesn't update prior captured function pointers)
            if (_txCount < 500_000 && Interlocked.Increment(ref _txCount) % 10_000 == 0)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug("Refreshing EVM instruction cache");
                }
                // Regenerate the non-traced opcode set to pick up any updated PGO optimized methods.
                table.NoTrace = GenerateOpCodes<TTracingInst>(spec);
            }
            // Ensure the non-traced opcode set is generated and assign it to the _opcodeMethods field.
            _opcodeMethods = table.NoTrace ??= GenerateOpCodes<TTracingInst>(spec);
        }
        else
        {
            // For tracing-enabled execution, generate (if necessary) and cache the traced opcode set.
            _opcodeMethods = table.Traced ??= GenerateOpCodes<TTracingInst>(spec);
        }
    }

    protected virtual delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref nint, EvmExceptionType>[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        EvmInstructions.GenerateOpCodes<TGasPolicy, TTracingInst>(spec);
}
