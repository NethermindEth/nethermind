// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Consensus.ProofAggregation;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Engine API <c>InclusionListProofStatusV1</c>: the result of <c>engine_verifyInclusionListProofV1</c> and
/// <c>engine_submitInclusionListProofV1</c> (execution-apis#945).</summary>
public sealed class InclusionListProofStatusV1
{
    /// <summary><see cref="PayloadStatus.Valid"/>, <see cref="PayloadStatus.Invalid"/>, or <see cref="ProofUnknownStatus"/> when the
    /// committed proof's bytes are not held, after which the consensus layer fetches the <c>il_proof</c> and calls again.</summary>
    public required string Status { get; init; }

    /// <summary>Why the status is <c>INVALID</c>, or <c>null</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ValidationError { get; init; }

    public static InclusionListProofStatusV1 Valid { get; } = new() { Status = PayloadStatus.Valid };

    public static InclusionListProofStatusV1 Invalid(string error) => new() { Status = PayloadStatus.Invalid, ValidationError = error };

    public const string ProofUnknownStatus = "PROOF_UNKNOWN";

    public static InclusionListProofStatusV1 ProofUnknown { get; } = new() { Status = ProofUnknownStatus };

    public static InclusionListProofStatusV1 From(InclusionListProofStatus status, string? reason) => status switch
    {
        InclusionListProofStatus.Valid => Valid,
        InclusionListProofStatus.Invalid => Invalid(reason ?? "invalid inclusion-list proof"),
        _ => ProofUnknown
    };
}
