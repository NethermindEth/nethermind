// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Result of <c>engine_verifyInclusionListProofV1</c>: whether a signed inclusion list's committed proof is valid.</summary>
public sealed class InclusionListProofStatusV1
{
    /// <summary><see cref="PayloadStatus.Valid"/> or <see cref="PayloadStatus.Invalid"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Why the proof is invalid, or <c>null</c> when it is valid.</summary>
    public string? ValidationError { get; init; }

    public static InclusionListProofStatusV1 Valid { get; } = new() { Status = PayloadStatus.Valid };

    public static InclusionListProofStatusV1 Invalid(string error) => new() { Status = PayloadStatus.Invalid, ValidationError = error };
}
