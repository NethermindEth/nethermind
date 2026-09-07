// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    // Weak keys: transient state-override specs in eth_simulateV1 must not be retained forever by this
    // process-wide cache.
    private static readonly ConditionalWeakTable<IReleaseSpec, OpcodeTable> _opcodeTablesBySpec = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private OpcodeTable GetOpcodeTable() =>
        _opcodeTablesBySpec.GetValue(Spec, static _ => new OpcodeTable());

    // How often the non-traced table is rebuilt, and when the rebuilding stops.
    private const long OpcodeRefreshInterval = 10_000;
    private const long OpcodeRefreshLimit = 500_000;

    private static long _txCount;

    /// <inheritdoc/>
    /// <remarks>
    /// Every 10,000 transactions, until 500,000 have run. A captured function pointer keeps pointing at the
    /// code it was taken from, so a table built during warm-up dispatches through tier-0 entry stubs for the
    /// life of the process; rebuilding it occasionally re-captures whatever the JIT has promoted since. The
    /// cadence stops once tiering has settled, and a rebuild is only a table of pointers.
    /// </remarks>
    private partial bool ShouldRefreshOpcodes()
    {
        if (_txCount >= OpcodeRefreshLimit || Interlocked.Increment(ref _txCount) % OpcodeRefreshInterval != 0)
            return false;

        if (_logger.IsDebug) _logger.Debug("Refreshing EVM instruction cache");
        return true;
    }

    public object ReturnData { get; set; }
}
