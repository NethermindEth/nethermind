// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Settlement;

public enum EezSettlementFailure
{
    /// <summary>The batch claims something the re-executed window does not show.</summary>
    Rejected,

    /// <summary>The submitted <c>postAndVerifyBatch</c> calldata is not a canonical encoding.</summary>
    InvalidPostBatch,

    /// <summary>The batch's DA payload is malformed or belongs to another rollup.</summary>
    InvalidDaPayload,

    /// <summary>The caller passed inputs that contradict each other, e.g. checkpoints the settling block cannot have.</summary>
    InternalInvariant,
}
