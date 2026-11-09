// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private const MethodImplOptions ExecutionHandlersInlining = MethodImplOptions.AggressiveInlining;

    public TransactionSubstate ExecuteTransaction(VmState<TGasPolicy> vmState, IWorldState worldState, ITxTracer txTracer) =>
        ExecuteTransaction<OffFlag>(vmState, worldState, txTracer);

    // Weak keys: transient state-override specs in eth_simulateV1 must not be retained forever by this
    // process-wide cache.
    private static readonly ConditionalWeakTable<IReleaseSpec, OpcodeTable> _opcodeTablesBySpec = [];

    private OpcodeTable? _cachedOpcodeTable;

    /// <remarks>
    /// Remembers the current spec's table so a run of transactions on one fork skips the weak-table lookup.
    /// <see cref="ResetSpecCaches"/> drops it when the spec changes, so it never outlives the block context that chose it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private OpcodeTable GetOpcodeTable() =>
        _cachedOpcodeTable ??= _opcodeTablesBySpec.GetValue(Spec, static _ => new OpcodeTable());

    partial void ResetSpecCaches()
    {
        _cachedOpcodeTable = null;
        _frameCodeBodies = null;
    }

    private const long OpcodeRefreshInterval = 10_000;
    private const long OpcodeRefreshLimit = 500_000;

    private static long _txCount;

    /// <inheritdoc/>
    /// <remarks>
    /// Startup heuristic for re-capturing opcode entry points as the runtime warms up.
    /// The transaction limits bound rebuild overhead; they do not detect JIT tier changes.
    /// </remarks>
    private partial bool ShouldRefreshOpcodes()
    {
        if (_txCount >= OpcodeRefreshLimit || Interlocked.Increment(ref _txCount) % OpcodeRefreshInterval != 0)
            return false;

        if (_logger.IsDebug) _logger.Debug("Refreshing EVM instruction cache");
        return true;
    }

    /// <summary>The largest nested RETURN/REVERT output the VM keeps a scratch buffer for between transactions.</summary>
    private const int MaxRetainedReturnDataLength = 32 * 1024;

    /// <inheritdoc/>
    /// <remarks>Every create output gets its own array.</remarks>
    private partial bool IsUndeployableCreateOutput(int length, bool isRevert) => false;

    public object? ReturnData { get; set; }

    private static partial delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, bool> SelectInitializeFrame(IReleaseSpec spec) =>
        SelectInitializeFrameCore(spec);
}
