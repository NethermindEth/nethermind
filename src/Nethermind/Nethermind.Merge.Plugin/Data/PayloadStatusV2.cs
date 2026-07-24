// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Result of <c>engine_newPayloadV6</c> (EIP-7805 / FOCIL). Extends <see cref="PayloadStatusV1"/>
/// with inclusion-list compliance reported alongside a still-<c>VALID</c> status.
/// </summary>
/// <remarks>
/// Per <see href="https://github.com/ethereum/execution-apis/pull/609">execution-apis#609</see>,
/// a valid payload that omitted an appendable inclusion-list transaction is reported as
/// <c>VALID</c> with <see cref="InclusionListSatisfied"/> set to <c>false</c>, replacing the
/// earlier non-standard <c>INCLUSION_LIST_UNSATISFIED</c> status value.
/// </remarks>
public class PayloadStatusV2 : PayloadStatusV1
{
    /// <summary>
    /// Whether the payload satisfied the inclusion-list constraints when <see cref="PayloadStatusV1.Status"/>
    /// is <c>VALID</c>; <c>null</c> for any other status.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? InclusionListSatisfied { get; set; }
}
