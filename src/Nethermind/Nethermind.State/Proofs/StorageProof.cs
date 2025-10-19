// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using System;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Proofs;

/// <summary>
/// EIP-1186 style storage proof
/// </summary>
public class StorageProof
{
    public ValueHash256? Key { get; set; }
    public byte[][]? Proof { get; set; }

    [JsonConverter(typeof(ProofStorageValueConverter))]
    public ReadOnlyMemory<byte>? Value { get; set; }
}
