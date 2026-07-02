// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// Optional execution-context fields the Taiko VM may provide to a precompile via
/// <see cref="IContextAwarePrecompile"/>. Each field is independently optional — a precompile
/// reads only what it needs and ignores the rest. Adding a new context field means adding a
/// member here, not a new interface.
/// </summary>
/// <remarks>
/// <para><see cref="RemainingGas"/> is the call-frame gas available before the precompile runs;
/// precompiles that report dynamic gas (e.g. L1STATICCALL) use it to clamp their L1 gas limit.</para>
/// <para><see cref="L1Origin"/> is the L1 block height anchored to the current L2 block, or
/// <c>null</c> when no origin is available (preconf blocks, <c>eth_call</c>, <c>debug_traceCall</c>
/// before the chain has any origins). Precompiles must treat <c>null</c> as permissive — the
/// proving layer enforces correctness in those contexts.</para>
/// </remarks>
public readonly struct PrecompileExtras(ulong remainingGas = 0, UInt256? l1Origin = null)
{
    public static readonly PrecompileExtras None = default;

    public readonly ulong RemainingGas = remainingGas;
    public readonly UInt256? L1Origin = l1Origin;
}
