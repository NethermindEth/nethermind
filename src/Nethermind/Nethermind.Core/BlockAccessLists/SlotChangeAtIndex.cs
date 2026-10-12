// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly record struct SlotChangeAtIndex(UInt256 Key, StorageChange Change)
{
    [JsonInclude]
    public readonly UInt256 Key = Key;

    [JsonInclude]
    public readonly StorageChange Change = Change;
}
