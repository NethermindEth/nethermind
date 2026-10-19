// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>EIP-8198: Quick Slots.</summary>
public static class Eip8198Constants
{
    /// <summary>
    /// Numerator of the maximum per-block base fee change, applied before
    /// <see cref="BaseFeeMaxChangeDenominator"/>.
    /// </summary>
    public static readonly UInt256 BaseFeeMaxChangeNumerator = 5;

    /// <summary>
    /// Denominator of the maximum per-block base fee change. Replaces the configured
    /// <see cref="Specs.IEip1559Spec.BaseFeeMaxChangeDenominator"/> while EIP-8198 is active.
    /// </summary>
    public static readonly UInt256 BaseFeeMaxChangeDenominator = 48;

    /// <summary>
    /// Slot length from the fork, in milliseconds, when the chainspec leaves <c>eip8198SlotDurationMs</c> unset:
    /// the mainnet value of the consensus-layer config <c>SLOT_DURATION_MS_EIP8198</c>.
    /// </summary>
    /// <remarks>A network config value rather than a protocol constant; see <see cref="Specs.ISpecProvider.Eip8198SlotDurationMs"/>.</remarks>
    public const ulong DefaultSlotDurationMs = 10_000;
}
