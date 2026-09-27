// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Settlement;

public enum EezSettlementFailure
{
    /// <summary>The batch claims something the re-executed window does not show.</summary>
    Rejected,

    /// <summary>The submitted calldata or DA payload is not a canonical encoding.</summary>
    InvalidCalldata,

    /// <summary>The caller passed inputs that contradict each other, e.g. checkpoints the settling block cannot have.</summary>
    InternalInvariant,
}
