// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using System;
using System.Text.Json.Serialization;

namespace Nethermind.State.Proofs;

/// <summary>
/// EIP-1186 style storage proof
/// </summary>
public class StorageProof
{
    /// <summary>The requested storage key, big-endian.</summary>
    /// <remarks>
    /// 32 bytes for a key sent with 63 or more hex digits, which execution-apis <c>eth_getProof</c> echoes as 32 bytes;
    /// otherwise without leading zeros, echoed as a quantity.
    /// </remarks>
    [JsonConverter(typeof(ProofStorageKeyConverter))]
    public byte[]? Key { get; set; }
    public byte[][] Proof { get; set; } = [];

    [JsonConverter(typeof(ProofStorageValueConverter))]
    public ReadOnlyMemory<byte>? Value { get; set; }
}
