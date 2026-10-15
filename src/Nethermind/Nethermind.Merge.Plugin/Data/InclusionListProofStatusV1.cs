// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Result of <c>engine_verifyInclusionListProofV1</c>: whether a signed inclusion list's commitment is valid.</summary>
public sealed class InclusionListProofStatusV1
{
    /// <summary><see cref="PayloadStatus.Valid"/>, <see cref="PayloadStatus.Invalid"/>, or <see cref="ProofUnknownStatus"/> when the
    /// committed proof is not held, after which the consensus layer fetches the <c>il_proof</c> and calls again.</summary>
    public required string Status { get; init; }

    /// <summary>Why the commitment is not valid, or <c>null</c> when it is.</summary>
    public string? ValidationError { get; init; }

    public static InclusionListProofStatusV1 Valid { get; } = new() { Status = PayloadStatus.Valid };

    public static InclusionListProofStatusV1 Invalid(string error) => new() { Status = PayloadStatus.Invalid, ValidationError = error };

    public const string ProofUnknownStatus = "PROOF_UNKNOWN";

    public static InclusionListProofStatusV1 ProofUnknown(string? reason) => new() { Status = ProofUnknownStatus, ValidationError = reason };
}
