// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly record struct BalanceChange([property: JsonPropertyName("index")] int BlockAccessIndex, [property: JsonPropertyName("value")] UInt256 PostBalance) : IIndexedChange
{
    public override string ToString() => $"{BlockAccessIndex}:{PostBalance}";
}
