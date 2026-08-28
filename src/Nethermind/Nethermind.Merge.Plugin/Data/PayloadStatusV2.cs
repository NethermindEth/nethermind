// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Result of <c>engine_newPayloadV6</c> (EIP-7805).</summary>
/// <remarks>A payload that omitted an appendable inclusion-list transaction is still <c>VALID</c>
/// (<see href="https://github.com/ethereum/execution-apis/pull/609">execution-apis#609</see>).</remarks>
public class PayloadStatusV2 : PayloadStatusV1
{
    /// <summary>Inclusion-list compliance when <see cref="PayloadStatusV1.Status"/> is <c>VALID</c>; <c>null</c> otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? InclusionListSatisfied { get; set; }
}
