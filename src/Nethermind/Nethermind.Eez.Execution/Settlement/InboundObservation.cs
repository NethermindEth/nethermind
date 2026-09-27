// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>What an executed inbound delivery on L2 proves, and the entry its DA sidecar must encode.</summary>
public sealed record InboundObservation(ValueHash256 CallHash, UInt256 Value, byte[] ReturnData, ExecutionEntry DerivedDaEntry);
