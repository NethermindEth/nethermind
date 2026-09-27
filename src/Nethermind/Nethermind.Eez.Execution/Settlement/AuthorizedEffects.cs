// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>An inbound delivery an entry claims, checked against what it did on L2.</summary>
public readonly record struct AuthorizedInbound(int TransactionIndex, InboundObservation Observation);

/// <summary>An outbound call an entry executes, the system load that stages it, and the entry its DA sidecar must carry.</summary>
public readonly record struct AuthorizedOutbound(int LoadTransactionIndex, int TransactionIndex, ExecutionEntry DerivedDaEntry);
