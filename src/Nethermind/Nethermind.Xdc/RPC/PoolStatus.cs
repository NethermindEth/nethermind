// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// Content of the vote and timeout message pools, keyed by the reference client's pool key.
/// </summary>
/// <remarks>
/// Mirrors the reference client's <c>MessageStatus</c>: votes are keyed
/// <c>{round}:{gapNumber}:{blockNumber}:{proposedBlockHash}</c> and timeouts <c>{round}:{gapNumber}</c>.
/// </remarks>
public class PoolStatus
{
    public IDictionary<string, SignerTypes>? Vote { get; set; }
    public IDictionary<string, SignerTypes>? Timeout { get; set; }
}
