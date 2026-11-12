// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The arguments of <c>EEZL2.executeIncomingCrossChainCall</c>, which delivers an inbound call on L2: the entry's
/// single incoming call is the delivered call.
/// </summary>
public sealed record IncomingCrossChainCall(L2ExecutionEntry[] Entries, L2StaticExecutionEntry[] StaticEntries);

/// <summary>The arguments of <c>EEZL2.loadExecutionTable</c>, which loads the entries outbound calls consume.</summary>
public sealed record ExecutionTable(L2ExecutionEntry[] Entries, L2StaticExecutionEntry[] StaticEntries);
