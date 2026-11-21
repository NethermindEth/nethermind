// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// A system transaction of the settling block that calls <c>executeIncomingCrossChainCall</c>. An invalid delivery
/// keeps its position and its reason, so it fails when an entry claims it rather than disappearing.
/// </summary>
/// <param name="Reverted">Whether the delivery reverted, the one failure a composer can repair by evicting the entry.</param>
public sealed record InboundCandidate(int TransactionIndex, InboundObservation? Observation, string? Error, bool Reverted);
