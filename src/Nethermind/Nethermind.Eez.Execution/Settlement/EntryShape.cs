// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Settlement;

public enum EntryShape
{
    Invalid,

    /// <summary>Moves the rollup to its settling block's parent without a cross-chain call.</summary>
    Anchor,

    /// <summary>Executes one flat call on L1 on behalf of an L2 contract.</summary>
    Outbound,

    /// <summary>Records the result of an L1 call delivered on L2.</summary>
    Inbound,
}
