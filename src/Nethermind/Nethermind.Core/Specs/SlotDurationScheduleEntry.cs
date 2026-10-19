// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// One entry of the slot duration schedule: from the first slot boundary at or after <paramref name="Timestamp"/>,
/// slots last <paramref name="SlotDurationMs"/>.
/// </summary>
/// <remarks>
/// Mirrors an entry of the consensus-layer <c>SLOT_DURATION_SCHEDULE</c> (EIP-8198), keyed by the activation timestamp
/// instead of the epoch.
/// </remarks>
/// <param name="Timestamp">Unix timestamp, in seconds, from which the duration applies.</param>
/// <param name="SlotDurationMs">Slot length in milliseconds; a positive whole number of seconds.</param>
public sealed record SlotDurationScheduleEntry(ulong Timestamp, ulong SlotDurationMs);
