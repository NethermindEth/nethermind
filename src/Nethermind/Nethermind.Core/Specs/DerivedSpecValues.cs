// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// Spec values derived from EIP flags rather than set per fork, so every way of enabling an EIP (a fork, a chainspec
/// transition or a test spec) gives the same values.
/// </summary>
/// <remarks>A later EIP that changes one of these values adds its flag here.</remarks>
public static class DerivedSpecValues
{
    /// <summary>
    /// The slot length, in milliseconds, under <paramref name="spec"/>: <paramref name="eip8198SlotDurationMs"/> under EIP-8198,
    /// otherwise <c>null</c> for the configured <c>Blocks.SecondsPerSlot</c>.
    /// </summary>
    /// <param name="spec">The active spec.</param>
    /// <param name="eip8198SlotDurationMs">The chain's slot length from EIP-8198, <see cref="ISpecProvider.Eip8198SlotDurationMs"/>.</param>
    /// <remarks>
    /// Only places slots, so it follows the chain's consensus layer: without a configured value the slot length stays
    /// <c>Blocks.SecondsPerSlot</c>. The base fee rule reads the slot duration from block spacing instead.
    /// </remarks>
    public static ulong? SlotDurationMs(IReleaseSpec spec, ulong? eip8198SlotDurationMs) =>
        spec.IsEip8198Enabled ? eip8198SlotDurationMs : null;
}
