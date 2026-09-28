// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    // Poll cancellation at the first taken jump once 1024 opcodes have run since the last poll. Code that
    // takes no jump only moves forward, so 1024 is not the bound between polls: one frame's straight-line
    // code is, up to the code (or initcode) size limit in opcodes, each of which may be expensive (an inline
    // precompile STATICCALL, a large KECCAK256 or MCOPY). Gas still bounds the total work; only the
    // cancellation latency grows.
    private const int CancellationPollInterval = 1024;

    /// <summary>The dispatch table the running transaction uses, resolved once by <c>PrepareOpcodes</c>.</summary>
    private delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] _opcodeHandlers = null!;

    private delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? _filteredOpcodeHandlers;
    private delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? _filteredTracedSource;
    private delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? _filteredSilentSource;
    private UInt256 _filteredInstructionMask;

    private struct SilentInstructionFlag : IFlag
    {
        public static bool IsActive => true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]
        GetOpcodeHandlers<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        GetOpcodeTable().GetHandlers<TTracingInst, TCancelable>(Spec);

    /// <summary>Whether the dispatch table should be rebuilt before the coming transaction.</summary>
    private partial bool ShouldRefreshOpcodes();

    /// <summary>Resolves the dispatch table the coming transaction runs on.</summary>
    /// <remarks>
    /// Once per transaction rather than once per frame: the tracing and cancellation flags hold for the
    /// whole transaction and the spec for the whole block, so every frame the transaction enters or
    /// resumes would resolve the same table, and the per-spec lookup behind it is not free.
    /// </remarks>
    private void PrepareOpcodes<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (DispatchFlags.Cancelable(_isCancelableCached))
            PrepareOpcodes<TTracingInst, OnFlag>();
        else
            PrepareOpcodes<TTracingInst, OffFlag>();
    }

    private void PrepareOpcodes<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        // The fork comes from Spec here and in GetOpcodeTable, so the cache key and the table contents
        // cannot describe different forks.
        IReleaseSpec spec = Spec;
        // Per transaction, not per table build: a cached table would otherwise let a later block
        // outside the compiled fork range run against rules that do not describe it.
        SpecFlags.Validate(spec);
        OpcodeTable table = GetOpcodeTable();

        // Traced tables are left alone: a tracing run is short, and rebuilding one would cost more than
        // the promoted code it could pick up.
        if (!TTracingInst.IsActive && ShouldRefreshOpcodes())
            table.RefreshNonTraced(spec);

        _executionHandlers = table.GetExecutionHandlers(spec);
        _opcodeHandlers = table.GetHandlers<TTracingInst, TCancelable>(spec);
        if (TTracingInst.IsActive && _txTracer is IInstructionTracingFilter filter)
        {
            UInt256 mask = filter.InstructionMask;
            if (mask != UInt256.MaxValue)
                PrepareFilteredOpcodes(mask, table.GetHandlers<SilentInstructionFlag, TCancelable>(spec));
        }
    }

    private void PrepareFilteredOpcodes(UInt256 mask,
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] silent)
    {
        if (_filteredTracedSource != _opcodeHandlers || _filteredSilentSource != silent || _filteredInstructionMask != mask)
        {
            _filteredOpcodeHandlers ??= new delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[256];
            _filteredTracedSource = _opcodeHandlers;
            _filteredSilentSource = silent;
            _filteredInstructionMask = mask;
            for (int opcode = 0; opcode < _filteredOpcodeHandlers.Length; opcode++)
            {
                _filteredOpcodeHandlers[opcode] = (mask & (UInt256.One << opcode)) != UInt256.Zero
                    ? _opcodeHandlers[opcode]
                    : silent[opcode];
            }
        }
        _opcodeHandlers = _filteredOpcodeHandlers!;
    }

    private sealed unsafe class OpcodeTable
    {
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? NoTrace;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? NoTraceCancelable;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? Traced;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? TracedCancelable;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? Silent;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? SilentCancelable;

        private ExecutionHandlers? _executionHandlers;

        public ExecutionHandlers GetExecutionHandlers(IReleaseSpec spec)
        {
            ExecutionHandlers? handlers = System.Threading.Volatile.Read(ref _executionHandlers);
            if (handlers is not null) return handlers;
            handlers = new ExecutionHandlers(spec);
            return System.Threading.Interlocked.CompareExchange(ref _executionHandlers, handlers, null) ?? handlers;
        }

        /// <summary>The table for this combination of flags, built on first use.</summary>
        /// <param name="spec">The fork whose opcode set the table describes.</param>
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]
            GetHandlers<TTracingInst, TCancelable>(IReleaseSpec spec)
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
        {
            ref delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[]? table =
                ref typeof(TTracingInst) == typeof(SilentInstructionFlag)
                    ? ref (TCancelable.IsActive ? ref SilentCancelable : ref Silent)
                    : ref TTracingInst.IsActive
                    ? ref (TCancelable.IsActive ? ref TracedCancelable : ref Traced)
                    : ref (TCancelable.IsActive ? ref NoTraceCancelable : ref NoTrace);

            return table ??= GenerateOpcodeHandlers<TTracingInst, TCancelable>(spec);
        }

        /// <summary>Rebuilds both non-traced tables from whatever the JIT has promoted since the last build.</summary>
        /// <remarks>
        /// A captured function pointer keeps pointing at the code it was taken from. Both non-traced tables
        /// share one cadence, so a rebuild has to cover both: which one a given transaction asks for follows
        /// the node's mix of block processing and RPC, and block processing is what the cadence exists for.
        /// The tracing flag is fixed to off here rather than taken from the caller, so a caller inside a
        /// traced run cannot fill the non-traced tables with tracing handlers.
        /// </remarks>
        public void RefreshNonTraced(IReleaseSpec spec)
        {
            NoTrace = GenerateOpcodeHandlers<OffFlag, OffFlag>(spec);
            NoTraceCancelable = GenerateOpcodeHandlers<OffFlag, OnFlag>(spec);
            System.Threading.Volatile.Write(ref _executionHandlers, null);
        }
    }
}
