// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>A nested operation: transfer (0), selfdestruct (1), CREATE (2), or CREATE2 (3).</summary>
/// <remarks>Operations describe execution, not committed balance changes; reverted operations are included.</remarks>
public sealed class OtsInternalOperation
{
    /// <summary>Transfer (0), selfdestruct (1), CREATE (2), or CREATE2 (3).</summary>
    public int Type { get; init; }
    /// <summary>The account initiating the operation.</summary>
    public required Address From { get; init; }
    /// <summary>The recipient, beneficiary, or created contract.</summary>
    public required Address To { get; init; }
    /// <summary>The operation's value in wei, including reverted execution.</summary>
    public UInt256 Value { get; init; }
}
