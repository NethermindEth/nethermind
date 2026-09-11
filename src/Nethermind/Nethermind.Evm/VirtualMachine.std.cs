// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private const MethodImplOptions ExecutionHandlersInlining = MethodImplOptions.AggressiveInlining;

    // Weak keys: transient state-override specs in eth_simulateV1 must not be retained forever by this
    // process-wide cache.
    private static readonly ConditionalWeakTable<IReleaseSpec, OpcodeTable> _opcodeTablesBySpec = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private OpcodeTable GetOpcodeTable() =>
        _opcodeTablesBySpec.GetValue(Spec, static _ => new OpcodeTable());

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

    public object? ReturnData { get; set; }
}
