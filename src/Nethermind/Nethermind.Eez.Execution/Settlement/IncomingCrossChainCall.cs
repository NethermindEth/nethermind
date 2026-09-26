// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>The arguments of <c>EEZL2.executeIncomingCrossChainCall</c>, which delivers an inbound call on L2.</summary>
public sealed record IncomingCrossChainCall(
    Address Destination,
    UInt256 Value,
    byte[] Data,
    Address SourceAddress,
    ulong SourceRollup,
    L2ExecutionEntry[] Entries,
    L2StaticExecutionEntry[] StaticEntries);

/// <summary>The arguments of <c>EEZL2.loadExecutionTable</c>, which loads the entries outbound calls consume.</summary>
public sealed record ExecutionTable(L2ExecutionEntry[] Entries, L2StaticExecutionEntry[] StaticEntries);
