// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>EIP-8198: Quick Slots.</summary>
public static class Eip8198Constants
{
    /// <summary>
    /// The slot length EIP-8198's maximum base fee change is designed for, giving 5/48 through
    /// <see cref="BaseFeeMaxChange.ScaledToSlot"/> in <see cref="Specs.DerivedSpecValues"/>.
    /// </summary>
    /// <remarks>
    /// A fork constant for the consensus base fee rule only. The chain's actual slot length comes from
    /// <see cref="Specs.ISpecProvider.Eip8198SlotDurationMs"/>.
    /// </remarks>
    public const ulong DesignSlotDurationMs = 10_000;
}
