// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Core;

/// <summary>An EIP-7805 omission claim: the payload index at which the builder's omission of a Profile 2
/// candidate is evaluated.</summary>
/// <remarks>Both fields are required on the wire: a claim missing either, or whose index is not a canonical
/// <c>QUANTITY</c>, does not match <c>OmissionClaimV1</c>.</remarks>
/// <param name="TransactionHash">Keccak-256 of the inclusion-list entry's EIP-2718 bytes.</param>
/// <param name="EvaluationIndex">An index into the payload's transactions, <c>0</c> before the first and
/// <c>len(payload.transactions)</c> at the end; a larger value is clamped to the end.</param>
public readonly record struct OmissionClaim(
    [property: JsonRequired] Hash256 TransactionHash,
    [property: JsonRequired, JsonConverter(typeof(CanonicalQuantityConverter))] ulong EvaluationIndex);
