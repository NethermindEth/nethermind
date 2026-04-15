// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Core.BlockAccessLists;

public readonly record struct NonceChange([property: JsonPropertyName("index")] int BlockAccessIndex, [property: JsonPropertyName("value")] ulong NewNonce) : IIndexedChange
{
    public override string ToString() => $"{BlockAccessIndex}:{NewNonce}";
}
