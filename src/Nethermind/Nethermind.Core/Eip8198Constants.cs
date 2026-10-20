// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>EIP-8198: Quick Slots.</summary>
public static class Eip8198Constants
{
    /// <summary>
    /// The slot length EIP-8198 sets, and the one its maximum base fee change is designed for: 5/48 through
    /// <see cref="BaseFeeMaxChange.ScaledToSlot"/>. Both are derived in <see cref="Specs.DerivedSpecValues"/>.
    /// </summary>
    /// <remarks>
    /// A fork constant. The slot length only places slots, while the base fee fraction is consensus.
    /// </remarks>
    public const ulong DesignSlotDurationMs = 10_000;
}
