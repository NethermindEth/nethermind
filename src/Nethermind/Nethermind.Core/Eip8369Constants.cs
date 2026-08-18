// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Includer VERIFY-budget bounds of EIP-8369. https://eips.ethereum.org/EIPS/eip-8369
/// </summary>
/// <remarks>
/// Distinct from EIP-8141's public-mempool <c>MAX_VERIFY_GAS</c>: these bound the inclusion-list
/// includer/validator surface, not mempool ingress.
/// </remarks>
public static class Eip8369Constants
{
    /// <summary>Per-inclusion-list VERIFY-budget the includer fills across Profile-2 frame transactions.</summary>
    public const ulong MaxVerifyGasPerIl = 1UL << 20;

    /// <summary>Upper bound on a single Profile-2 transaction's VERIFY-budget cost.</summary>
    public const ulong MaxVerifyGasPerTx = 1UL << 20;
}
