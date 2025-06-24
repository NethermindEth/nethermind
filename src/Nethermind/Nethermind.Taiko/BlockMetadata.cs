// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Taiko;

public class BlockMetadata
{
    public required Address Beneficiary { get; set; }

    public required long GasLimit { get; set; }
    public required ulong Timestamp { get; set; }

    public required Hash256 MixHash { get; set; }

    public required byte[] TxList { get; set; }

    [JsonConverter(typeof(Base64Converter))]
    public required byte[] ExtraData { get; set; }
}
