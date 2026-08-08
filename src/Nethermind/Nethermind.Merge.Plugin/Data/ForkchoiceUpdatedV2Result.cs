// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Result of <c>engine_forkchoiceUpdatedV5</c> (EIP-7805 / FOCIL). Mirrors
/// <see cref="ForkchoiceUpdatedV1Result"/> but carries a <see cref="PayloadStatusV2"/> so a
/// <c>VALID</c> head can report inclusion-list compliance per
/// <see href="https://github.com/ethereum/execution-apis/pull/609">execution-apis#609</see>.
/// </summary>
public class ForkchoiceUpdatedV2Result
{
    /// <summary>Fork-choice status, including inclusion-list compliance for a <c>VALID</c> head.</summary>
    public PayloadStatusV2 PayloadStatus { get; set; } = new() { Status = Data.PayloadStatus.Syncing };

    /// <summary>Identifier of the payload build process, or <c>null</c> if there is none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PayloadId { get; set; }

    /// <summary>
    /// Maps a base fork-choice result to the V5 shape, attaching the head block's inclusion-list
    /// compliance (<paramref name="inclusionListSatisfied"/> is <c>null</c> for a non-<c>VALID</c>
    /// head or when no retained result is available).
    /// </summary>
    public static ForkchoiceUpdatedV2Result From(ForkchoiceUpdatedV1Result result, bool? inclusionListSatisfied) => new()
    {
        PayloadId = result.PayloadId,
        PayloadStatus = new PayloadStatusV2
        {
            Status = result.PayloadStatus.Status,
            LatestValidHash = result.PayloadStatus.LatestValidHash,
            ValidationError = result.PayloadStatus.ValidationError,
            InclusionListSatisfied = inclusionListSatisfied,
        }
    };
}
