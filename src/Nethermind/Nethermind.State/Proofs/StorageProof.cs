// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using System;
using System.Text.Json.Serialization;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style storage proof
    /// </summary>
    public class StorageProof
    {
        public byte[]? Key { get; set; }
        public byte[][]? Proof { get; set; }
        [JsonConverter(typeof(NullableByteReadOnlyMemoryConverter2))]
        public ReadOnlyMemory<byte>? Value { get; set; }
    }
}
