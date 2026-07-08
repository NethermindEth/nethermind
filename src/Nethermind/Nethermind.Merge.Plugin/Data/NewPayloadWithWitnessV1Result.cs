// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Result of <c>engine_newPayloadWithWitness</c>.
/// Combines the standard <see cref="PayloadStatusV1"/> fields with an optional
/// <see cref="Witness"/> that is populated when <see cref="Status"/> is
/// <see cref="PayloadStatus.Valid"/>.
/// <seealso href="https://github.com/ethereum/execution-apis/pull/773"/>
/// </summary>
public class NewPayloadWithWitnessV1Result : IDisposable
{
    public string Status { get; set; } = PayloadStatus.Invalid;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? LatestValidHash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ValidationError { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Witness? ExecutionWitness { get; set; }

    public static NewPayloadWithWitnessV1Result FromPayloadStatus(PayloadStatusV1 status, Witness? witness = null) =>
        new()
        {
            Status = status.Status,
            LatestValidHash = status.LatestValidHash,
            ValidationError = status.ValidationError,
            ExecutionWitness = witness
        };

    public void Dispose() => ExecutionWitness?.Dispose();
}
