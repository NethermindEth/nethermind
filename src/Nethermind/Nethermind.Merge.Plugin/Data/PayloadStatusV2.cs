// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Result of <c>engine_newPayloadV6</c> (EIP-7805).</summary>
/// <remarks>Inclusion is checked apart from execution, so a payload that omitted an appendable inclusion-list
/// transaction is still <c>VALID</c>, and its verdict may still be pending when execution is answered.</remarks>
public class PayloadStatusV2 : PayloadStatusV1
{
    /// <summary>The completed inclusion verdict for <see cref="InclusionCheckId"/>; <c>null</c> while it is pending or
    /// when execution is not <c>VALID</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? InclusionListSatisfied { get; set; }

    /// <summary>The identifier <c>engine_getInclusionListVerdictV1</c> polls the inclusion check by, present exactly
    /// when execution is <c>VALID</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? InclusionCheckId { get; set; }
}
